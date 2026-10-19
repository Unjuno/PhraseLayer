using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;

const string source = "alpha beta gamma, delta epsilon zeta eta, theta iota kappa lambda mu.";
const int profileCount = 256;
const int encounterBudget = 20;
const int seed = 0x51A57E55;
const int difficultySortDecimals = 12;
const double epsilon = 1e-12;

var document = new RuleBasedSemanticSegmenter().Segment(source);
var words = document.OfKind(SemanticUnitKind.Word).ToArray();
if (words.Length != 12)
    throw new InvalidOperationException("Selection comparison fixture requires exactly 12 atomic words.");
var totalTokens = words.Sum(word => word.TokenCount);

var strategies = new[]
{
    SelectionStrategy.ProductionGreedy,
    SelectionStrategy.ClosestHardestPrefix,
    SelectionStrategy.NoOvershootHardestPrefix,
};
var accumulators = strategies.ToDictionary(strategy => strategy, strategy => new StrategyAccumulator(strategy));
var random = new Random(seed);

for (var profile = 0; profile < profileCount; profile++)
{
    var initial = new double[words.Length];
    for (var index = 0; index < initial.Length; index++)
        initial[index] = 0.05 + random.NextDouble() * 0.75;

    foreach (var strategy in strategies)
    {
        var result = RunProfile(profile, initial, strategy);
        accumulators[strategy].Add(result);
    }
}

var reports = accumulators.Values.Select(accumulator => accumulator.Build()).ToArray();
var production = reports.Single(report => report.Strategy == SelectionStrategy.ProductionGreedy.ToString());
var closest = reports.Single(report => report.Strategy == SelectionStrategy.ClosestHardestPrefix.ToString());
var noOvershoot = reports.Single(report => report.Strategy == SelectionStrategy.NoOvershootHardestPrefix.ToString());

if (production.SelectedRatioIncreaseTransitions == 0)
    throw new InvalidOperationException("Baseline fixture stopped reproducing semantic-span selected-ratio overshoot.");
if (closest.MeanAbsoluteTargetError > production.MeanAbsoluteTargetError + epsilon)
    throw new InvalidOperationException("Closest-prefix strategy unexpectedly increased mean target error.");
if (noOvershoot.MeanPositiveOvershoot > production.MeanPositiveOvershoot + epsilon)
    throw new InvalidOperationException("No-overshoot-prefix strategy unexpectedly increased mean positive overshoot.");

Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "pass",
    experiment = "semantic-assistance-budget-selector-comparison",
    scope = "deterministic-core-policy-counterfactual",
    source,
    seed,
    profiles = profileCount,
    encounter_budget = encounterBudget,
    total_atomic_tokens = totalTokens,
    candidate_priority_preserved = true,
    easier_candidate_substitution_allowed = false,
    semantic_span_splitting_allowed = false,
    character_percentage_selection_used = false,
    strategies = reports,
    production_code_changed_by_counterfactual = false,
    human_learning_effectiveness_measured = false,
    quest_execution_performed = false,
}));

ProfileResult RunProfile(int profile, double[] initial, SelectionStrategy strategy)
{
    var learner = new InMemoryLearnerModel(0.20);
    for (var index = 0; index < words.Length; index++)
        learner.SetUnderstanding(words[index].Text, initial[index]);

    var planner = new AssistancePlanner();
    var adaptation = new LearnerAdaptationEngine(learner);
    var previousSelected = double.NaN;
    var previousTarget = double.PositiveInfinity;
    var previousMean = words.Average(word => learner.Estimate(word).Understanding);
    var selectedIncreases = 0;
    var maxIncrease = 0.0;
    var observations = 0;
    var transitions = 0;
    var absoluteTargetErrorSum = 0.0;
    var positiveOvershootSum = 0.0;
    var positiveOvershootObservations = 0;
    var maxPositiveOvershoot = 0.0;
    var reachedZeroAt = (int?)null;
    var examples = new List<IncreaseExample>();

    for (var encounter = 1; encounter <= encounterBudget; encounter++)
    {
        var basePlan = planner.Plan(document, learner, AssistancePolicy.ForMode(AssistanceMode.Auto));
        var selectedPlan = Select(basePlan, strategy, totalTokens);
        var selected = selectedPlan.SelectedRatio;
        var target = selectedPlan.TargetRatio;
        var mean = words.Average(word => learner.Estimate(word).Understanding);

        if (target > previousTarget + epsilon)
            throw new InvalidOperationException($"Positive evidence increased Auto target: strategy={strategy}, profile={profile}, encounter={encounter}.");
        if (mean + epsilon < previousMean)
            throw new InvalidOperationException($"Positive evidence decreased mean understanding: strategy={strategy}, profile={profile}, encounter={encounter}.");

        observations++;
        var targetError = Math.Abs(selected - target);
        absoluteTargetErrorSum += targetError;
        var overshoot = Math.Max(0.0, selected - target);
        if (overshoot > 0)
        {
            positiveOvershootSum += overshoot;
            positiveOvershootObservations++;
            if (overshoot > maxPositiveOvershoot)
                maxPositiveOvershoot = overshoot;
        }

        if (!double.IsNaN(previousSelected))
        {
            transitions++;
            if (selected > previousSelected + epsilon)
            {
                var jump = selected - previousSelected;
                selectedIncreases++;
                if (jump > maxIncrease)
                    maxIncrease = jump;
                if (examples.Count < 4)
                {
                    examples.Add(new IncreaseExample(
                        profile,
                        encounter,
                        previousSelected,
                        selected,
                        previousTarget,
                        target,
                        previousMean,
                        mean,
                        selectedPlan.Decisions.Select(decision => decision.Unit.Text).ToArray()));
                }
            }
        }

        if (selected <= epsilon)
        {
            reachedZeroAt = encounter;
            break;
        }

        var encounterPlan = BuildLearningPlan(selectedPlan);
        var session = new LearningEncounterSession(encounterPlan, adaptation);
        foreach (var decision in selectedPlan.Decisions)
            session.Record(decision.Unit, LearningEvidenceKind.RecallSucceeded);
        session.Finish(successfulUnassistedCompletion: true);

        previousSelected = selected;
        previousTarget = target;
        previousMean = mean;
    }

    return new ProfileResult(
        strategy,
        observations,
        transitions,
        selectedIncreases,
        maxIncrease,
        absoluteTargetErrorSum,
        positiveOvershootSum,
        positiveOvershootObservations,
        maxPositiveOvershoot,
        reachedZeroAt,
        examples.ToArray());
}

AssistancePlan Select(AssistancePlan productionPlan, SelectionStrategy strategy, int atomicTokens)
{
    if (strategy == SelectionStrategy.ProductionGreedy || productionPlan.Decisions.Count == 0)
        return productionPlan;

    var priority = productionPlan.Decisions
        .OrderByDescending(decision => Math.Round(decision.Difficulty, difficultySortDecimals, MidpointRounding.AwayFromZero))
        .ThenByDescending(decision => decision.Unit.Kind)
        .ThenBy(decision => decision.Unit.Start)
        .ToArray();
    var targetTokens = Math.Max(1, (int)Math.Ceiling(atomicTokens * productionPlan.TargetRatio));

    var cumulative = new int[priority.Length];
    var tokens = 0;
    for (var index = 0; index < priority.Length; index++)
    {
        tokens += priority[index].Unit.TokenCount;
        cumulative[index] = tokens;
    }

    var prefixLength = 1;
    if (strategy == SelectionStrategy.ClosestHardestPrefix)
    {
        var bestDistance = Math.Abs(cumulative[0] - targetTokens);
        for (var index = 1; index < cumulative.Length; index++)
        {
            var distance = Math.Abs(cumulative[index] - targetTokens);
            // Equal-distance ties deliberately prefer more support rather than silently under-supporting.
            if (distance < bestDistance || (distance == bestDistance && cumulative[index] > cumulative[prefixLength - 1]))
            {
                bestDistance = distance;
                prefixLength = index + 1;
            }
        }
    }
    else if (strategy == SelectionStrategy.NoOvershootHardestPrefix)
    {
        prefixLength = 1; // A single indivisible semantic unit may itself exceed the target.
        for (var index = 0; index < cumulative.Length; index++)
        {
            if (cumulative[index] <= targetTokens)
                prefixLength = index + 1;
            else
                break;
        }
    }
    else
    {
        throw new ArgumentOutOfRangeException(nameof(strategy));
    }

    var chosen = priority.Take(prefixLength).OrderBy(decision => decision.Unit.Start).ToArray();
    var chosenTokens = chosen.Sum(decision => decision.Unit.TokenCount);
    return new AssistancePlan(chosen, productionPlan.TargetRatio, Math.Min(1.0, chosenTokens / (double)atomicTokens));
}

MixedLanguagePlan BuildLearningPlan(AssistancePlan assistance)
{
    var segments = new List<MixedLanguageSegment>();
    var cursor = 0;
    foreach (var decision in assistance.Decisions.OrderBy(decision => decision.Unit.Start))
    {
        var unit = decision.Unit;
        if (unit.Start > cursor)
        {
            var untouched = source.Substring(cursor, unit.Start - cursor);
            segments.Add(new MixedLanguageSegment(untouched, untouched, false, null));
        }
        segments.Add(new MixedLanguageSegment(unit.Text, unit.Text, true, unit));
        cursor = unit.End;
    }
    if (cursor < source.Length)
    {
        var rest = source.Substring(cursor);
        segments.Add(new MixedLanguageSegment(rest, rest, false, null));
    }
    if (segments.Count == 0)
        segments.Add(new MixedLanguageSegment(source, source, false, null));
    return new MixedLanguagePlan(source, segments, assistance, document);
}

enum SelectionStrategy
{
    ProductionGreedy = 0,
    ClosestHardestPrefix = 1,
    NoOvershootHardestPrefix = 2,
}

sealed record IncreaseExample(
    int Profile,
    int Encounter,
    double PreviousSelectedRatio,
    double SelectedRatio,
    double PreviousTargetRatio,
    double TargetRatio,
    double PreviousMeanUnderstanding,
    double MeanUnderstanding,
    string[] SelectedSemanticSpans);

sealed record ProfileResult(
    SelectionStrategy Strategy,
    int Observations,
    int Transitions,
    int SelectedIncreases,
    double MaxSelectedIncrease,
    double AbsoluteTargetErrorSum,
    double PositiveOvershootSum,
    int PositiveOvershootObservations,
    double MaxPositiveOvershoot,
    int? ReachedZeroAt,
    IncreaseExample[] Examples);

sealed class StrategyAccumulator
{
    private int profiles;
    private int observations;
    private int transitions;
    private int profilesWithIncrease;
    private int increaseTransitions;
    private double maxIncrease;
    private double absoluteTargetErrorSum;
    private double positiveOvershootSum;
    private int positiveOvershootObservations;
    private double maxPositiveOvershoot;
    private int profilesReachingZero;
    private int maxEncountersToZero;
    private readonly List<IncreaseExample> examples = new List<IncreaseExample>();

    public StrategyAccumulator(SelectionStrategy strategy) { Strategy = strategy; }
    public SelectionStrategy Strategy { get; }

    public void Add(ProfileResult result)
    {
        profiles++;
        observations += result.Observations;
        transitions += result.Transitions;
        if (result.SelectedIncreases > 0)
            profilesWithIncrease++;
        increaseTransitions += result.SelectedIncreases;
        if (result.MaxSelectedIncrease > maxIncrease)
            maxIncrease = result.MaxSelectedIncrease;
        absoluteTargetErrorSum += result.AbsoluteTargetErrorSum;
        positiveOvershootSum += result.PositiveOvershootSum;
        positiveOvershootObservations += result.PositiveOvershootObservations;
        if (result.MaxPositiveOvershoot > maxPositiveOvershoot)
            maxPositiveOvershoot = result.MaxPositiveOvershoot;
        if (result.ReachedZeroAt.HasValue)
        {
            profilesReachingZero++;
            if (result.ReachedZeroAt.Value > maxEncountersToZero)
                maxEncountersToZero = result.ReachedZeroAt.Value;
        }
        foreach (var example in result.Examples)
            if (examples.Count < 6) examples.Add(example);
    }

    public StrategyReport Build() => new StrategyReport(
        Strategy.ToString(),
        profiles,
        observations,
        transitions,
        profilesWithIncrease,
        increaseTransitions,
        transitions == 0 ? 0.0 : increaseTransitions / (double)transitions,
        maxIncrease,
        observations == 0 ? 0.0 : absoluteTargetErrorSum / observations,
        positiveOvershootObservations == 0 ? 0.0 : positiveOvershootSum / positiveOvershootObservations,
        positiveOvershootObservations,
        maxPositiveOvershoot,
        profilesReachingZero,
        maxEncountersToZero,
        examples.ToArray());
}

sealed record StrategyReport(
    string Strategy,
    int Profiles,
    int Observations,
    int Transitions,
    int ProfilesWithSelectedRatioIncrease,
    int SelectedRatioIncreaseTransitions,
    double SelectedRatioIncreaseRate,
    double MaxSelectedRatioIncrease,
    double MeanAbsoluteTargetError,
    double MeanPositiveOvershoot,
    int PositiveOvershootObservations,
    double MaxPositiveOvershoot,
    int ProfilesReachingZeroAssistance,
    int MaxEncountersToZero,
    IncreaseExample[] SampleIncreases);
