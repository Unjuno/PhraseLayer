using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;

const int profilesPerFixture = 128;
const int encounterBudget = 20;
const int allowedIncreaseTokens = 2;
const int baseSeed = 0x4A71C2D;
const int difficultySortDecimals = 12;
const double epsilon = 1e-12;

var fixtures = new[]
{
    new Fixture("clauses_3_4_5", "alpha beta gamma, delta epsilon zeta eta, theta iota kappa lambda mu.", Array.Empty<string>()),
    new Fixture("clauses_4_4_4", "alpha beta gamma delta, epsilon zeta eta theta, iota kappa lambda mu.", Array.Empty<string>()),
    new Fixture("clauses_2_3_7", "alpha beta, gamma delta epsilon, zeta eta theta iota kappa lambda mu.", Array.Empty<string>()),
    new Fixture("word_only_13", "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu.", Array.Empty<string>()),
    new Fixture("mwe_mixed", "please keep off grass, now turn left gently, emergency exit ahead nearby.", new[] { "keep off", "turn left", "emergency exit" }),
    new Fixture("short_keep_off", "KEEP OFF", new[] { "keep off" }),
    new Fixture("short_emergency_exit", "EMERGENCY EXIT", new[] { "emergency exit" }),
};

var fixtureReports = new List<object>();
var aggregateProduction = new Aggregate();
var aggregateGuard = new Aggregate();
var fixturesWhereGuardDominates = 0;
var fixturesWhereGuardRegresses = 0;

for (var fixtureIndex = 0; fixtureIndex < fixtures.Length; fixtureIndex++)
{
    var fixture = fixtures[fixtureIndex];
    var segmenter = new RuleBasedSemanticSegmenter(fixture.MultiwordExpressions);
    var document = segmenter.Segment(fixture.Source);
    var atoms = BuildAtomicUnits(document).ToArray();
    if (atoms.Length == 0)
        throw new InvalidOperationException("Robustness fixture has no atomic semantic units: " + fixture.Name);
    var totalTokens = atoms.Sum(unit => unit.TokenCount);
    var keys = atoms.Select(unit => unit.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var random = new Random(unchecked(baseSeed + fixtureIndex * 104729));
    var initialProfiles = new Dictionary<string, double>[profilesPerFixture];
    for (var profile = 0; profile < profilesPerFixture; profile++)
    {
        var initial = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
            initial[key] = 0.05 + random.NextDouble() * 0.75;
        initialProfiles[profile] = initial;
    }

    var production = RunStrategy(fixture, document, atoms, totalTokens, initialProfiles, useGuard: false);
    var guard = RunStrategy(fixture, document, atoms, totalTokens, initialProfiles, useGuard: true);
    aggregateProduction.Add(production);
    aggregateGuard.Add(guard);

    var guardDominates =
        guard.IncreaseTransitions <= production.IncreaseTransitions &&
        guard.MaxIncrease <= production.MaxIncrease + epsilon &&
        guard.MeanAbsoluteTargetError <= production.MeanAbsoluteTargetError + epsilon &&
        guard.MaxEncountersToZero <= production.MaxEncountersToZero;
    var guardRegresses =
        guard.IncreaseTransitions > production.IncreaseTransitions ||
        guard.MaxIncrease > production.MaxIncrease + epsilon ||
        guard.MeanAbsoluteTargetError > production.MeanAbsoluteTargetError + epsilon ||
        guard.MaxEncountersToZero > production.MaxEncountersToZero;
    if (guardDominates) fixturesWhereGuardDominates++;
    if (guardRegresses) fixturesWhereGuardRegresses++;

    fixtureReports.Add(new
    {
        fixture = fixture.Name,
        source = fixture.Source,
        multiword_expressions = fixture.MultiwordExpressions,
        atomic_units = atoms.Select(unit => new { text = unit.Text, kind = unit.Kind.ToString(), tokens = unit.TokenCount }).ToArray(),
        total_atomic_tokens = totalTokens,
        production,
        local_guard_allowance_2 = guard,
        guard_dominates_on_reviewed_metrics = guardDominates,
        guard_regresses_on_any_reviewed_metric = guardRegresses,
    });
}

var aggregateProductionReport = aggregateProduction.Build("ProductionGreedy");
var aggregateGuardReport = aggregateGuard.Build("LocalIncreaseGuardAllowance2");
var consistentBenefit = fixturesWhereGuardDominates == fixtures.Length && fixturesWhereGuardRegresses == 0;
var aggregateImprovement =
    aggregateGuardReport.IncreaseRate <= aggregateProductionReport.IncreaseRate &&
    aggregateGuardReport.MaxIncrease <= aggregateProductionReport.MaxIncrease + epsilon &&
    aggregateGuardReport.MeanAbsoluteTargetError <= aggregateProductionReport.MeanAbsoluteTargetError + epsilon &&
    aggregateGuardReport.MaxEncountersToZero <= aggregateProductionReport.MaxEncountersToZero;

Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "pass",
    experiment = "semantic-assistance-local-guard-multi-fixture-robustness",
    scope = "deterministic-core-policy-counterfactual",
    base_seed = baseSeed,
    fixtures = fixtures.Length,
    profiles_per_fixture = profilesPerFixture,
    encounter_budget = encounterBudget,
    allowed_increase_tokens = allowedIncreaseTokens,
    previous_ratio_state_scope = "same-source-trajectory-only",
    cross_source_hysteresis_forbidden = true,
    semantic_span_splitting_allowed = false,
    character_percentage_selection_used = false,
    fixture_results = fixtureReports,
    aggregate = new { production = aggregateProductionReport, local_guard_allowance_2 = aggregateGuardReport },
    fixtures_where_guard_dominates = fixturesWhereGuardDominates,
    fixtures_where_guard_regresses = fixturesWhereGuardRegresses,
    consistent_benefit_across_all_fixtures = consistentBenefit,
    aggregate_improvement_on_all_reviewed_metrics = aggregateImprovement,
    production_change_recommended = consistentBenefit && aggregateImprovement,
    human_learning_effectiveness_measured = false,
    quest_execution_performed = false,
}));

StrategyReport RunStrategy(
    Fixture fixture,
    SemanticDocument document,
    SemanticUnit[] atoms,
    int totalTokens,
    Dictionary<string, double>[] initialProfiles,
    bool useGuard)
{
    var aggregate = new Aggregate();
    for (var profile = 0; profile < initialProfiles.Length; profile++)
    {
        var learner = new InMemoryLearnerModel(0.20);
        foreach (var pair in initialProfiles[profile])
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
            else
            {
                plan = productionPlan;
            }

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

AssistancePlan ApplyGuard(
    AssistancePlan productionPlan,
    double previousSelected,
    int atomicTokens,
    out bool intervened,
    out bool forcedIndivisibleIncrease)
{
    intervened = false;
    forcedIndivisibleIncrease = false;
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
        forcedIndivisibleIncrease = priority[0].Unit.TokenCount > previousTokens + allowedIncreaseTokens;
    }
    var chosen = priority.Take(bestLength).OrderBy(decision => decision.Unit.Start).ToArray();
    var chosenTokens = chosen.Sum(decision => decision.Unit.TokenCount);
    return new AssistancePlan(chosen, productionPlan.TargetRatio, Math.Min(1.0, chosenTokens / (double)atomicTokens));
}

static IEnumerable<SemanticUnit> BuildAtomicUnits(SemanticDocument document)
{
    var mwes = document.OfKind(SemanticUnitKind.MultiwordExpression).OrderBy(unit => unit.Start).ToArray();
    foreach (var mwe in mwes) yield return mwe;
    foreach (var word in document.OfKind(SemanticUnitKind.Word))
        if (!mwes.Any(mwe => mwe.Overlaps(word))) yield return word;
}

static double WeightedMean(IEnumerable<SemanticUnit> atoms, ILearnerModel learner)
{
    var tokenCount = 0;
    var weighted = 0.0;
    foreach (var atom in atoms)
    {
        tokenCount += atom.TokenCount;
        weighted += learner.Estimate(atom).Understanding * atom.TokenCount;
    }
    return tokenCount == 0 ? 1.0 : weighted / tokenCount;
}

static void RequireExactSemanticSpans(string sourceText, AssistancePlan plan)
{
    foreach (var decision in plan.Decisions)
    {
        if (decision.Unit.Start < 0 || decision.Unit.End > sourceText.Length)
            throw new InvalidOperationException("Assistance span escaped source bounds.");
        if (!string.Equals(sourceText.Substring(decision.Unit.Start, decision.Unit.Length), decision.Unit.Text, StringComparison.Ordinal))
            throw new InvalidOperationException("Assistance decision is no longer an exact source semantic span.");
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
    if (segments.Count == 0)
        segments.Add(new MixedLanguageSegment(sourceText, sourceText, false, null));
    return new MixedLanguagePlan(sourceText, segments, assistance, document);
}

sealed record Fixture(string Name, string Source, string[] MultiwordExpressions);

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
    private int profiles;
    private int observations;
    private int transitions;
    private int increases;
    private double maxIncrease;
    private double absoluteTargetError;
    private int interventions;
    private int forced;
    private int zeroCount;
    private int maxZero;

    public void Add(ProfileMetrics result)
    {
        profiles++;
        observations += result.Observations;
        transitions += result.Transitions;
        increases += result.IncreaseTransitions;
        if (result.MaxIncrease > maxIncrease) maxIncrease = result.MaxIncrease;
        absoluteTargetError += result.AbsoluteTargetError;
        interventions += result.Interventions;
        forced += result.ForcedIndivisibleIncreases;
        if (result.ZeroAt.HasValue)
        {
            zeroCount++;
            if (result.ZeroAt.Value > maxZero) maxZero = result.ZeroAt.Value;
        }
    }

    public void Add(StrategyReport report)
    {
        profiles += report.Profiles;
        observations += report.Observations;
        transitions += report.Transitions;
        increases += report.IncreaseTransitions;
        if (report.MaxIncrease > maxIncrease) maxIncrease = report.MaxIncrease;
        absoluteTargetError += report.MeanAbsoluteTargetError * report.Observations;
        interventions += report.Interventions;
        forced += report.ForcedIndivisibleIncreases;
        zeroCount += report.ProfilesReachingZero;
        if (report.MaxEncountersToZero > maxZero) maxZero = report.MaxEncountersToZero;
    }

    public StrategyReport Build(string strategy) => new StrategyReport(
        strategy,
        profiles,
        observations,
        transitions,
        increases,
        transitions == 0 ? 0.0 : increases / (double)transitions,
        maxIncrease,
        observations == 0 ? 0.0 : absoluteTargetError / observations,
        interventions,
        forced,
        zeroCount,
        maxZero);
}

sealed record StrategyReport(
    string Strategy,
    int Profiles,
    int Observations,
    int Transitions,
    int IncreaseTransitions,
    double IncreaseRate,
    double MaxIncrease,
    double MeanAbsoluteTargetError,
    int Interventions,
    int ForcedIndivisibleIncreases,
    int ProfilesReachingZero,
    int MaxEncountersToZero);
