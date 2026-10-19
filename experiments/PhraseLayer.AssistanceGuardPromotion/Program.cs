using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;

const int profilesPerSeed = 64;
const int seedCount = 8;
const int encounterBudget = 20;
const int allowedIncreaseTokens = 2;
const int baseSeed = 0x27C4A51;
const int difficultySortDecimals = 12;
const double epsilon = 1e-12;

var fixtures = new[]
{
    new Fixture("clauses_3_4_5", "alpha beta gamma, delta epsilon zeta eta, theta iota kappa lambda mu.", Array.Empty<string>()),
    new Fixture("clauses_4_4_4", "alpha beta gamma delta, epsilon zeta eta theta, iota kappa lambda mu.", Array.Empty<string>()),
    new Fixture("clauses_2_3_7", "alpha beta, gamma delta epsilon, zeta eta theta iota kappa lambda mu.", Array.Empty<string>()),
    new Fixture("clauses_5_6_9", "alpha beta gamma delta epsilon, zeta eta theta iota kappa lambda, mu nu xi omicron pi rho sigma tau upsilon.", Array.Empty<string>()),
    new Fixture("clauses_1_5_11", "alpha, beta gamma delta epsilon zeta, eta theta iota kappa lambda mu nu xi omicron pi rho.", Array.Empty<string>()),
    new Fixture("word_only_13", "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu.", Array.Empty<string>()),
    new Fixture("word_only_25", "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon phi chi psi omega cedar.", Array.Empty<string>()),
    new Fixture("mwe_2word", "please keep off grass, now turn left gently, emergency exit ahead nearby.", new[] { "keep off", "turn left", "emergency exit" }),
    new Fixture("mwe_3word", "please do not enter lobby, system remains out of service now, emergency exit stays ahead.", new[] { "do not enter", "out of service", "emergency exit" }),
    new Fixture("short_keep_off", "KEEP OFF", new[] { "keep off" }),
    new Fixture("short_emergency_exit", "EMERGENCY EXIT", new[] { "emergency exit" }),
};

var cells = new List<CellReport>();
var aggregateProduction = new Aggregate();
var aggregateGuard = new Aggregate();
var regressionCells = 0;
var dominanceCells = 0;
var interventionCells = 0;

for (var fixtureIndex = 0; fixtureIndex < fixtures.Length; fixtureIndex++)
{
    var fixture = fixtures[fixtureIndex];
    var segmenter = new RuleBasedSemanticSegmenter(fixture.MultiwordExpressions);
    var document = segmenter.Segment(fixture.Source);
    var atoms = BuildAtomicUnits(document).ToArray();
    if (atoms.Length == 0) throw new InvalidOperationException("No atomic units: " + fixture.Name);
    var totalTokens = atoms.Sum(unit => unit.TokenCount);
    var keys = atoms.Select(unit => unit.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    for (var seedIndex = 0; seedIndex < seedCount; seedIndex++)
    {
        var seed = unchecked(baseSeed + fixtureIndex * 104729 + seedIndex * 130363);
        var profiles = GenerateProfiles(keys, seed, profilesPerSeed);
        var production = RunStrategy(fixture, document, atoms, totalTokens, profiles, false);
        var guard = RunStrategy(fixture, document, atoms, totalTokens, profiles, true);
        aggregateProduction.Add(production);
        aggregateGuard.Add(guard);
        var dominates = Dominates(guard, production);
        var regresses = Regresses(guard, production);
        if (dominates) dominanceCells++;
        if (regresses) regressionCells++;
        if (guard.Interventions > 0) interventionCells++;
        cells.Add(new CellReport(fixture.Name, seedIndex, seed, totalTokens, production, guard, dominates, regresses));
    }
}

var productionAggregate = aggregateProduction.Build("ProductionGreedy");
var guardAggregate = aggregateGuard.Build("LocalIncreaseGuardAllowance2");
var aggregateImprovement = Dominates(guardAggregate, productionAggregate);
var crossSource = BuildCrossSourceCounterexample();
if (!crossSource.GlobalCarryOverWouldUnderAssist)
    throw new InvalidOperationException("Cross-source anti-hysteresis counterexample no longer reproduces.");

var candidateForIntegration = regressionCells == 0 && aggregateImprovement && interventionCells > 0;
Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "pass",
    experiment = "semantic-assistance-local-guard-promotion-stress",
    scope = "deterministic-core-policy-counterfactual",
    fixtures = fixtures.Length,
    seeds_per_fixture = seedCount,
    profiles_per_seed = profilesPerSeed,
    total_profiles_per_strategy = fixtures.Length * seedCount * profilesPerSeed,
    encounter_budget = encounterBudget,
    allowed_increase_tokens = allowedIncreaseTokens,
    state_scope_required = "same-source-or-stable-track-only",
    cross_source_state_reuse_allowed = false,
    semantic_span_splitting_allowed = false,
    character_percentage_selection_used = false,
    cells_tested = cells.Count,
    cells_where_guard_dominates = dominanceCells,
    cells_where_guard_regresses = regressionCells,
    cells_with_actual_guard_intervention = interventionCells,
    aggregate = new { production = productionAggregate, local_guard_allowance_2 = guardAggregate },
    cross_source_counterexample = crossSource,
    candidate_for_product_integration = candidateForIntegration,
    production_code_changed_by_counterfactual = false,
    human_learning_effectiveness_measured = false,
    quest_execution_performed = false,
    cell_results = cells,
}));

Dictionary<string, double>[] GenerateProfiles(string[] keys, int seed, int count)
{
    var random = new Random(seed);
    var result = new Dictionary<string, double>[count];
    for (var profile = 0; profile < count; profile++)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys) values[key] = 0.05 + random.NextDouble() * 0.75;
        result[profile] = values;
    }
    return result;
}

StrategyReport RunStrategy(Fixture fixture, SemanticDocument document, SemanticUnit[] atoms, int totalTokens,
    Dictionary<string, double>[] initialProfiles, bool useGuard)
{
    var aggregate = new Aggregate();
    foreach (var initial in initialProfiles)
    {
        var learner = new InMemoryLearnerModel(0.20);
        foreach (var pair in initial) learner.SetUnderstanding(pair.Key, pair.Value);
        var planner = new AssistancePlanner();
        var adaptation = new LearnerAdaptationEngine(learner);
        var previousSelected = double.NaN;
        var previousTarget = double.PositiveInfinity;
        var previousMean = WeightedMean(atoms, learner);
        var metrics = new ProfileMetrics();

        for (var encounter = 1; encounter <= encounterBudget; encounter++)
        {
            var production = planner.Plan(document, learner, AssistancePolicy.ForMode(AssistanceMode.Auto));
            AssistancePlan plan;
            if (useGuard)
            {
                plan = ApplyGuard(production, previousSelected, totalTokens, out var intervened, out var forced);
                if (intervened) metrics.Interventions++;
                if (forced) metrics.ForcedIndivisibleIncreases++;
            }
            else plan = production;

            var selected = plan.SelectedRatio;
            var target = plan.TargetRatio;
            var mean = WeightedMean(atoms, learner);
            if (target > previousTarget + epsilon) throw new InvalidOperationException("Target increased: " + fixture.Name);
            if (mean + epsilon < previousMean) throw new InvalidOperationException("Understanding decreased: " + fixture.Name);
            RequireExactSemanticSpans(fixture.Source, plan);

            metrics.Observations++;
            metrics.AbsoluteTargetError += Math.Abs(selected - target);
            if (!double.IsNaN(previousSelected))
            {
                metrics.Transitions++;
                if (selected > previousSelected + epsilon)
                {
                    metrics.IncreaseTransitions++;
                    metrics.MaxIncrease = Math.Max(metrics.MaxIncrease, selected - previousSelected);
                }
            }
            if (selected <= epsilon)
            {
                metrics.ZeroAt = encounter;
                break;
            }

            var session = new LearningEncounterSession(BuildLearningPlan(fixture.Source, document, plan), adaptation);
            foreach (var decision in plan.Decisions) session.Record(decision.Unit, LearningEvidenceKind.RecallSucceeded);
            session.Finish(successfulUnassistedCompletion: true);
            previousSelected = selected;
            previousTarget = target;
            previousMean = mean;
        }
        aggregate.Add(metrics);
    }
    return aggregate.Build(useGuard ? "LocalIncreaseGuardAllowance2" : "ProductionGreedy");
}

AssistancePlan ApplyGuard(AssistancePlan production, double previousSelected, int atomicTokens, out bool intervened, out bool forced)
{
    intervened = false;
    forced = false;
    if (double.IsNaN(previousSelected) || production.Decisions.Count == 0) return production;
    var previousTokens = (int)Math.Round(previousSelected * atomicTokens, MidpointRounding.AwayFromZero);
    var allowedTokens = Math.Min(atomicTokens, previousTokens + allowedIncreaseTokens);
    var productionTokens = (int)Math.Round(production.SelectedRatio * atomicTokens, MidpointRounding.AwayFromZero);
    if (productionTokens <= allowedTokens) return production;

    intervened = true;
    var priority = production.Decisions
        .OrderByDescending(decision => Math.Round(decision.Difficulty, difficultySortDecimals, MidpointRounding.AwayFromZero))
        .ThenByDescending(decision => decision.Unit.Kind)
        .ThenBy(decision => decision.Unit.Start)
        .ToArray();
    var cumulative = 0;
    var bestLength = 0;
    for (var index = 0; index < priority.Length; index++)
    {
        cumulative += priority[index].Unit.TokenCount;
        if (cumulative <= allowedTokens) bestLength = index + 1;
        else break;
    }
    if (bestLength == 0)
    {
        bestLength = 1;
        forced = priority[0].Unit.TokenCount > previousTokens + allowedIncreaseTokens;
    }
    var chosen = priority.Take(bestLength).OrderBy(decision => decision.Unit.Start).ToArray();
    var tokens = chosen.Sum(decision => decision.Unit.TokenCount);
    return new AssistancePlan(chosen, production.TargetRatio, Math.Min(1.0, tokens / (double)atomicTokens));
}

CrossSourceCounterexample BuildCrossSourceCounterexample()
{
    var hard = fixtures.Single(fixture => fixture.Name == "clauses_5_6_9");
    var document = new RuleBasedSemanticSegmenter().Segment(hard.Source);
    var atoms = BuildAtomicUnits(document).ToArray();
    var production = new AssistancePlanner().Plan(document, new InMemoryLearnerModel(0.05), AssistancePolicy.ForMode(AssistanceMode.Auto));
    var guarded = ApplyGuard(production, 0.0, atoms.Sum(unit => unit.TokenCount), out _, out var forced);
    return new CrossSourceCounterexample(0.0, production.TargetRatio, production.SelectedRatio, guarded.SelectedRatio,
        production.SelectedRatio - guarded.SelectedRatio, forced, guarded.SelectedRatio + epsilon < production.SelectedRatio);
}

bool Dominates(StrategyReport guard, StrategyReport production) =>
    guard.IncreaseTransitions <= production.IncreaseTransitions &&
    guard.MaxIncrease <= production.MaxIncrease + epsilon &&
    guard.MeanAbsoluteTargetError <= production.MeanAbsoluteTargetError + epsilon &&
    guard.ProfilesReachingZero >= production.ProfilesReachingZero &&
    guard.MaxEncountersToZero <= production.MaxEncountersToZero;

bool Regresses(StrategyReport guard, StrategyReport production) =>
    guard.IncreaseTransitions > production.IncreaseTransitions ||
    guard.MaxIncrease > production.MaxIncrease + epsilon ||
    guard.MeanAbsoluteTargetError > production.MeanAbsoluteTargetError + epsilon ||
    guard.ProfilesReachingZero < production.ProfilesReachingZero ||
    guard.MaxEncountersToZero > production.MaxEncountersToZero;

static IEnumerable<SemanticUnit> BuildAtomicUnits(SemanticDocument document)
{
    var mwes = document.OfKind(SemanticUnitKind.MultiwordExpression).OrderBy(unit => unit.Start).ToArray();
    foreach (var mwe in mwes) yield return mwe;
    foreach (var word in document.OfKind(SemanticUnitKind.Word)) if (!mwes.Any(mwe => mwe.Overlaps(word))) yield return word;
}

static double WeightedMean(IEnumerable<SemanticUnit> atoms, ILearnerModel learner)
{
    var tokens = 0; var sum = 0.0;
    foreach (var atom in atoms) { tokens += atom.TokenCount; sum += learner.Estimate(atom).Understanding * atom.TokenCount; }
    return tokens == 0 ? 1.0 : sum / tokens;
}

static void RequireExactSemanticSpans(string source, AssistancePlan plan)
{
    foreach (var decision in plan.Decisions)
        if (decision.Unit.Start < 0 || decision.Unit.End > source.Length ||
            !string.Equals(source.Substring(decision.Unit.Start, decision.Unit.Length), decision.Unit.Text, StringComparison.Ordinal))
            throw new InvalidOperationException("Assistance decision is not an exact semantic source span.");
}

static MixedLanguagePlan BuildLearningPlan(string source, SemanticDocument document, AssistancePlan assistance)
{
    var segments = new List<MixedLanguageSegment>();
    var cursor = 0;
    foreach (var decision in assistance.Decisions.OrderBy(decision => decision.Unit.Start))
    {
        var unit = decision.Unit;
        if (unit.Start > cursor) { var text = source.Substring(cursor, unit.Start - cursor); segments.Add(new MixedLanguageSegment(text, text, false, null)); }
        segments.Add(new MixedLanguageSegment(unit.Text, unit.Text, true, unit));
        cursor = unit.End;
    }
    if (cursor < source.Length) { var text = source.Substring(cursor); segments.Add(new MixedLanguageSegment(text, text, false, null)); }
    if (segments.Count == 0) segments.Add(new MixedLanguageSegment(source, source, false, null));
    return new MixedLanguagePlan(source, segments, assistance, document);
}

sealed record Fixture(string Name, string Source, string[] MultiwordExpressions);
sealed record CellReport(string Fixture, int SeedIndex, int Seed, int TotalTokens, StrategyReport Production, StrategyReport Guard, bool GuardDominates, bool GuardRegresses);
sealed record CrossSourceCounterexample(double PreviousUnrelatedSourceSelectedRatio, double HardNewSourceTargetRatio, double HardNewSourceProductionRatio, double HardNewSourceWronglyGuardedRatio, double UnderAssistanceDelta, bool ForcedIndivisibleSpan, bool GlobalCarryOverWouldUnderAssist);
sealed class ProfileMetrics { public int Observations, Transitions, IncreaseTransitions, Interventions, ForcedIndivisibleIncreases; public double MaxIncrease, AbsoluteTargetError; public int? ZeroAt; }
sealed class Aggregate
{
    private int profiles, observations, transitions, increases, interventions, forced, zeroCount, maxZero;
    private double maxIncrease, error;
    public void Add(ProfileMetrics x) { profiles++; observations += x.Observations; transitions += x.Transitions; increases += x.IncreaseTransitions; maxIncrease = Math.Max(maxIncrease, x.MaxIncrease); error += x.AbsoluteTargetError; interventions += x.Interventions; forced += x.ForcedIndivisibleIncreases; if (x.ZeroAt.HasValue) { zeroCount++; maxZero = Math.Max(maxZero, x.ZeroAt.Value); } }
    public void Add(StrategyReport x) { profiles += x.Profiles; observations += x.Observations; transitions += x.Transitions; increases += x.IncreaseTransitions; maxIncrease = Math.Max(maxIncrease, x.MaxIncrease); error += x.MeanAbsoluteTargetError * x.Observations; interventions += x.Interventions; forced += x.ForcedIndivisibleIncreases; zeroCount += x.ProfilesReachingZero; maxZero = Math.Max(maxZero, x.MaxEncountersToZero); }
    public StrategyReport Build(string strategy) => new StrategyReport(strategy, profiles, observations, transitions, increases, transitions == 0 ? 0 : increases / (double)transitions, maxIncrease, observations == 0 ? 0 : error / observations, interventions, forced, zeroCount, maxZero);
}
sealed record StrategyReport(string Strategy, int Profiles, int Observations, int Transitions, int IncreaseTransitions, double IncreaseRate, double MaxIncrease, double MeanAbsoluteTargetError, int Interventions, int ForcedIndivisibleIncreases, int ProfilesReachingZero, int MaxEncountersToZero);
