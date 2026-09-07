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
    if (atoms.Length == 0)
        throw new InvalidOperationException("Promotion fixture has no atomic semantic units: " + fixture.Name);
    var totalTokens = atoms.Sum(unit => unit.TokenCount);
    var keys = atoms.Select(unit => unit.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    for (var seedIndex = 0; seedIndex < seedCount; seedIndex++)
    {
        var seed = unchecked(baseSeed + fixtureIndex * 104729 + seedIndex * 130363);
        var initialProfiles = GenerateProfiles(keys, seed, profilesPerSeed);
        var production = RunStrategy(fixture, document, atoms, totalTokens, initialProfiles, useGuard: false);
        var guard = RunStrategy(fixture, document, atoms, totalTokens, initialProfiles, useGuard: true);
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
var crossSourceCounterexample = BuildCrossSourceCounterexample();
if (!crossSourceCounterexample.GlobalCarryOverWouldUnderAssist)
    throw new InvalidOperationException("Cross-source anti-hysteresis fixture stopped demonstrating under-assistance.");

var candidateForIntegration =
    regressionCells == 0 &&
    aggregateImprovement &&
    interventionCells > 0 &&
    crossSourceCounterexample.GlobalCarryOverWouldUnderAssist;

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
    cross_source_counterexample = crossSourceCounterexample,
    candidate_for_product_integration = candidateForIntegration,
    production_code_changed_by_counterfactual = false,
    human_learning_effectiveness_measured = false,
    quest_execution_performed = false,
    cell_results = cells,
}));

Dictionary<string, double>[] GenerateProfiles(string[] keys, int seed, int count)
{
    var random = new Random(seed);
    var profiles = new Dictionary<string, double>[count];
    for (var profile = 0; profile < count; profile++)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
            values[key] = 0.05 + random.NextDouble() * 0.75;
        profiles[profile] = values;
    }
    return profiles;
}

StrategyReport RunStrategy(
    Fixture fixture,
    SemanticDocument document,
    SemanticUnit[] atoms,
    int totalTokens,
    Dictionary<string, double>[] initialProfiles,
    bool useGuard)
{
    var aggregate = new Aggregate();
    foreach (var initial in initialProfiles)
    {
        var learner = new InMemoryLearnerModel(0.20);
        foreach (var pair in initial)
            learner.SetUnderstanding(pair.Key, pair.Value);
        var planner = new AssistancePlanner();
        var adaptation = new LearnerAdaptationEngine(learner);
        var previousSelected = double.NaN;
        var previousTarget = double.PositiveInfinity;
        var previousMean = WeightedMean(atoms, learner);
        var result = new ProfileMetrics();

        for (var encounter = 1; encounter <= encounterBudget; encounter++)
        {
            var productionPlan = planner.Plan(document, learner, AssistancePolicy.ForMode(AssistanceMode.Auto));
            AssistancePlan plan;
            if (useGuard)
            {
                plan = ApplyGuard(productionPlan, previousSelected, totalTokens, out var intervened, out var forced);
                if (intervened) result.Interventions++;
                if (forced) result.ForcedIndivisibleIncreases++;
            }
            else plan = productionPlan;

            var selected = plan.SelectedRatio;
            var target = plan.TargetRatio;
            var mean = WeightedMean(atoms, learner);
            if (target > previousTarget + epsilon)
                throw new InvalidOperationException("Positive-only evidence increased Auto target: " + fixture.Name);
            if (mean + epsilon < previousMean)
                throw new InvalidOperationException("Positive-only evidence decreased atomic understanding: " + fixture.Name);
            RequireExactSemanticSpans(fixture.Source, plan);

            result.Observations++;
            result.AbsoluteTargetError += Math.Abs(selected - target);
            if (!double.IsNaN(previousSelected))
            {
                result.Transitions++;
                if (selected > previousSelected + epsilon)
                {
                    result.IncreaseTransitions++;
                    var jump = selected - previousSelected;
                    if (jump > result.MaxIncrease) result.MaxIncrease = jump;
                }
            }

            if (selected <= epsilon)
            {
                result.ZeroAt = encounter;
                break;
            }

            var mixedPlan = BuildLearningPlan(fixture.Source, document, plan);
            var session = new LearningEncounterSession(mixedPlan, adaptation);
            foreach (var decision in plan.Decisions)
                session.Record(decision.Unit, LearningEvidenceKind.RecallSucceeded);
            session.Finish(successfulUnassistedCompletion: true);

            previousSelected = selected;
            previousTarget = target;
            previousMean = mean;
        }
        aggregate.Add(result);
    }
    return aggregate.Build(useGuard ? "LocalIncreaseGuardAllowance2" : "ProductionGreedy");
}

AssistancePlan ApplyGuard(AssistancePlan productionPlan, double previousSelected, int atomicTokens, out bool intervened, out bool forced)
{
    intervened = false;
    forced = false;
    if (double.IsNaN(previousSelected) || productionPlan.Decisions.Count == 0)
        return productionPlan;

    var previousTokens = (int)Math.Round(previousSelected * atomicTokens, MidpointRounding.AwayFromZero);
    var allowedTokens = Math.Min(atomicTokens, previousTokens + allowedIncreaseTokens);
    var productionTokens = (int)Math.Round(productionPlan.SelectedRatio * atomicTokens, MidpointRounding.AwayFromZero);
    if (productionTokens <= allowedTokens)
        return productionPlan;

    intervened = true;
    var priority = productionPlan.Decisions
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
    var chosenTokens = chosen.Sum(decision => decision.Unit.TokenCount);
    return new AssistancePlan(chosen, productionPlan.TargetRatio, Math.Min(1.0, chosenTokens / (double)atomicTokens));
}

CrossSourceCounterexample BuildCrossSourceCounterexample()
{
    var hard = fixtures.Single(fixture => fixture.Name == "clauses_5_6_9");
    var segmenter = new RuleBasedSemanticSegmenter();
    var document = segmenter.Segment(hard.Source);
    var atoms = BuildAtomicUnits(document).ToArray();
    var learner = new InMemoryLearnerModel(0.05);
    var production = new AssistancePlanner().Plan(document, learner, AssistancePolicy.ForMode(AssistanceMode.Auto));
    var guarded = ApplyGuard(production, previousSelected: 0.0, atoms.Sum(unit => unit.TokenCount), out _, out var forced);
    return new CrossSourceCounterexample(
        previous_unrelated_source_selected_ratio: 0.0,
        hard_new_source_target_ratio: production.TargetRatio,
        hard_new_source_production_ratio: production.SelectedRatio,
        hard_new_source_wrongly_guarded_ratio: guarded.SelectedRatio,
        under_assistance_delta: production.SelectedRatio - guarded.SelectedRatio,
        forced_indivisible_span: forced,
        global_carry_over_would_under_assist: guarded.SelectedRatio + epsilon < production.SelectedRatio);
}

static bool Dominates(StrategyReport guard, StrategyReport production) =>
    guard.IncreaseTransitions <= production.IncreaseTransitions &&
    guard.MaxIncrease <= production.MaxIncrease + epsilon &&
    guard.MeanAbsoluteTargetError <= production.MeanAbsoluteTargetError + epsilon &&
    guard.MaxEncountersToZero <= production.MaxEncountersToZero;

static bool Regresses(StrategyReport guard, StrategyReport production) =>
    guard.IncreaseTransitions > production.IncreaseTransitions ||
    guard.MaxIncrease > production.MaxIncrease + epsilon ||
    guard.MeanAbsoluteTargetError > production.MeanAbsoluteTargetError + epsilon ||
    guard.MaxEncountersToZero > production.MaxEncountersToZero;

static IEnumerable<SemanticUnit> BuildAtomicUnits(SemanticDocument document)
{
    var mwes = document.OfKind(SemanticUnitKind.MultiwordExpression).OrderBy(unit => unit.Start).ToArray();
    foreach (var mwe in mwes) yield return mwe;
    foreach (var word in document.OfKind(SemanticUnitKind.Word))
        if (!mwes.Any(mwe => mwe.Overlaps(word))) yield return word;
}

static double WeightedMean(IEnumerable<SemanticUnit> atoms, ILearnerModel learner)
{
    var tokens = 0;
    var sum = 0.0;
    foreach (var atom in atoms)
    {
        tokens += atom.TokenCount;
        sum += learner.Estimate(atom).Understanding * atom.TokenCount;
    }
    return tokens == 0 ? 1.0 : sum / tokens;
}

static void RequireExactSemanticSpans(string sourceText, AssistancePlan plan)
{
    foreach (var decision in plan.Decisions)
    {
        if (decision.Unit.Start < 0 || decision.Unit.End > sourceText.Length ||
            !string.Equals(sourceText.Substring(decision.Unit.Start, decision.Unit.Length), decision.Unit.Text, StringComparison.Ordinal))
            throw new InvalidOperationException("Assistance decision is not an exact semantic source span.");
    }
}

static MixedLanguagePlan BuildLearningPlan(string sourceText, SemanticDocument document, AssistancePlan assistance)
{
    var segments = new List<MixedLanguageSegment>();
    var cursor = 0;
    foreach (var decision in assistance.Decisions.OrderBy(decision => decision.Unit.Start))
    {
        var unit = decision.Unit;
        if (unit.Start > cursor)
        {
            var untouched = sourceText.Substring(cursor, unit.Start - cursor);
            segments.Add(new MixedLanguageSegment(untouched, untouched, false, null));
        }
        segments.Add(new MixedLanguageSegment(unit.Text, unit.Text, true, unit));
        cursor = unit.End;
    }
    if (cursor < sourceText.Length)
    {
        var rest = sourceText.Substring(cursor);
        segments.Add(new MixedLanguageSegment(rest, rest, false, null));
    }
    if (segments.Count == 0) segments.Add(new MixedLanguageSegment(sourceText, sourceText, false, null));
    return new MixedLanguagePlan(sourceText, segments, assistance, document);
}

sealed record Fixture(string Name, string Source, string[] MultiwordExpressions);
sealed record CellReport(string Fixture, int SeedIndex, int Seed, int TotalTokens, StrategyReport Production, StrategyReport Guard, bool GuardDominates, bool GuardRegresses);
sealed record CrossSourceCounterexample(double PreviousUnrelatedSourceSelectedRatio, double HardNewSourceTargetRatio, double HardNewSourceProductionRatio, double HardNewSourceWronglyGuardedRatio, double UnderAssistanceDelta, bool ForcedIndivisibleSpan, bool GlobalCarryOverWouldUnderAssist);

sealed class ProfileMetrics
{
    public int Observations;
    public int Transitions;
    public int IncreaseTransitions;
    public double MaxIncrease;
    public double AbsoluteTargetError;
    public int Interventions;
    public int ForcedIndivisibleIncreases;
    public int? ZeroAt;
}

sealed class Aggregate
{
    private int profiles, observations, transitions, increases, interventions, forced, zeroCount, maxZero;
    private double maxIncrease, absoluteTargetError;
    public void Add(ProfileMetrics result)
    {
        profiles++; observations += result.Observations; transitions += result.Transitions; increases += result.IncreaseTransitions;
        if (result.MaxIncrease > maxIncrease) maxIncrease = result.MaxIncrease;
        absoluteTargetError += result.AbsoluteTargetError; interventions += result.Interventions; forced += result.ForcedIndivisibleIncreases;
        if (result.ZeroAt.HasValue) { zeroCount++; if (result.ZeroAt.Value > maxZero) maxZero = result.ZeroAt.Value; }
    }
    public void Add(StrategyReport report)
    {
        profiles += report.Profiles; observations += report.Observations; transitions += report.Transitions; increases += report.IncreaseTransitions;
        if (report.MaxIncrease > maxIncrease) maxIncrease = report.MaxIncrease;
        absoluteTargetError += report.MeanAbsoluteTargetError * report.Observations; interventions += report.Interventions;
        forced += report.ForcedIndivisibleIncreases; zeroCount += report.ProfilesReachingZero;
        if (report.MaxEncountersToZero > maxZero) maxZero = report.MaxEncountersToZero;
    }
    public StrategyReport Build(string strategy) => new StrategyReport(strategy, profiles, observations, transitions, increases,
        transitions == 0 ? 0.0 : increases / (double)transitions, maxIncrease,
        observations == 0 ? 0.0 : absoluteTargetError / observations, interventions, forced, zeroCount, maxZero);
}

sealed record StrategyReport(string Strategy, int Profiles, int Observations, int Transitions, int IncreaseTransitions, double IncreaseRate,
    double MaxIncrease, double MeanAbsoluteTargetError, int Interventions, int ForcedIndivisibleIncreases, int ProfilesReachingZero, int MaxEncountersToZero);
