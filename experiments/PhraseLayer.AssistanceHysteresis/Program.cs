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

var lambdas = new[] { 0.0, 0.25, 0.5, 1.0, 2.0, 4.0, 8.0, 16.0 };
var segmenter = new RuleBasedSemanticSegmenter();
var document = segmenter.Segment(source);
var words = document.OfKind(SemanticUnitKind.Word).ToArray();
if (words.Length != 12)
    throw new InvalidOperationException("Hysteresis fixture requires exactly 12 atomic words.");
var totalTokens = words.Sum(word => word.TokenCount);

var random = new Random(seed);
var initialProfiles = new double[profileCount][];
for (var profile = 0; profile < profileCount; profile++)
{
    var initial = new double[words.Length];
    for (var index = 0; index < initial.Length; index++)
        initial[index] = 0.05 + random.NextDouble() * 0.75;
    initialProfiles[profile] = initial;
}

var productionAccumulator = new Accumulator("ProductionGreedy", null);
for (var profile = 0; profile < profileCount; profile++)
    productionAccumulator.Add(RunProfile(profile, initialProfiles[profile], null));

var lambdaReports = new List<StrategyReport>();
foreach (var lambda in lambdas)
{
    var accumulator = new Accumulator("StabilityPenalty", lambda);
    for (var profile = 0; profile < profileCount; profile++)
        accumulator.Add(RunProfile(profile, initialProfiles[profile], lambda));
    lambdaReports.Add(accumulator.Build());
}

var production = productionAccumulator.Build();
var zeroPenalty = lambdaReports.Single(report => report.Lambda == 0.0);
if (zeroPenalty.MeanAbsoluteTargetError > production.MeanAbsoluteTargetError + epsilon)
    throw new InvalidOperationException("Zero-penalty prefix selector should not increase mean target error over production greedy on this fixture.");
if (lambdaReports.Any(report => report.ProfilesReachingZeroAssistance != profileCount))
    throw new InvalidOperationException("A hysteresis setting prevented at least one positive-evidence profile from reaching zero assistance within the reviewed budget.");

var bestStability = lambdaReports
    .OrderBy(report => report.SelectedRatioIncreaseTransitions)
    .ThenBy(report => report.MeanAbsoluteTargetError)
    .ThenBy(report => report.Lambda)
    .First();
var pareto = lambdaReports
    .Where(candidate => !lambdaReports.Any(other =>
        other.SelectedRatioIncreaseTransitions <= candidate.SelectedRatioIncreaseTransitions &&
        other.MeanAbsoluteTargetError <= candidate.MeanAbsoluteTargetError + epsilon &&
        (other.SelectedRatioIncreaseTransitions < candidate.SelectedRatioIncreaseTransitions ||
         other.MeanAbsoluteTargetError < candidate.MeanAbsoluteTargetError - epsilon)))
    .Select(report => report.Lambda)
    .ToArray();

Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "pass",
    experiment = "semantic-assistance-temporal-hysteresis-lambda-sweep",
    scope = "deterministic-core-policy-counterfactual",
    source,
    seed,
    profiles = profileCount,
    encounter_budget = encounterBudget,
    total_atomic_tokens = totalTokens,
    objective = "abs(selected_ratio-target_ratio) + lambda*max(0, selected_ratio-previous_selected_ratio)",
    semantic_span_splitting_allowed = false,
    character_percentage_selection_used = false,
    candidate_priority_preserved_as_hardest_first_prefix = true,
    zero_assistance_allowed_when_candidates_exist = false,
    production = production,
    lambda_sweep = lambdaReports,
    best_stability = bestStability,
    pareto_lambdas = pareto,
    production_code_changed_by_counterfactual = false,
    human_learning_effectiveness_measured = false,
    quest_execution_performed = false,
}));

ProfileResult RunProfile(int profile, double[] initial, double? lambda)
{
    var learner = new InMemoryLearnerModel(0.20);
    for (var index = 0; index < words.Length; index++)
        learner.SetUnderstanding(words[index].Text, initial[index]);

    var planner = new AssistancePlanner();
    var adaptation = new LearnerAdaptationEngine(learner);
    var previousSelected = double.NaN;
    var previousTarget = double.PositiveInfinity;
    var previousMean = words.Average(word => learner.Estimate(word).Understanding);
    var observations = 0;
    var transitions = 0;
    var selectedIncreases = 0;
    var maxIncrease = 0.0;
    var absoluteTargetErrorSum = 0.0;
    var positiveOvershootSum = 0.0;
    var positiveOvershootObservations = 0;
    var undershootSum = 0.0;
    var undershootObservations = 0;
    var maxOvershoot = 0.0;
    var forcedMinimumSpanIncreases = 0;
    int? zeroAt = null;

    for (var encounter = 1; encounter <= encounterBudget; encounter++)
    {
        var productionPlan = planner.Plan(document, learner, AssistancePolicy.ForMode(AssistanceMode.Auto));
        var selectedPlan = lambda.HasValue
            ? SelectWithPenalty(productionPlan, previousSelected, lambda.Value, totalTokens)
            : productionPlan;
        var selected = selectedPlan.SelectedRatio;
        var target = selectedPlan.TargetRatio;
        var mean = words.Average(word => learner.Estimate(word).Understanding);

        if (target > previousTarget + epsilon)
            throw new InvalidOperationException($"Positive evidence increased Auto target: profile={profile}, encounter={encounter}, lambda={lambda}.");
        if (mean + epsilon < previousMean)
            throw new InvalidOperationException($"Positive evidence decreased mean understanding: profile={profile}, encounter={encounter}, lambda={lambda}.");

        observations++;
        absoluteTargetErrorSum += Math.Abs(selected - target);
        if (selected > target + epsilon)
        {
            var overshoot = selected - target;
            positiveOvershootSum += overshoot;
            positiveOvershootObservations++;
            if (overshoot > maxOvershoot) maxOvershoot = overshoot;
        }
        else if (selected + epsilon < target)
        {
            undershootSum += target - selected;
            undershootObservations++;
        }

        if (!double.IsNaN(previousSelected))
        {
            transitions++;
            if (selected > previousSelected + epsilon)
            {
                selectedIncreases++;
                var jump = selected - previousSelected;
                if (jump > maxIncrease) maxIncrease = jump;

                if (lambda.HasValue && selectedPlan.Decisions.Count > 0)
                {
                    var minimumPrefixRatio = selectedPlan.Decisions
                        .OrderByDescending(decision => Math.Round(decision.Difficulty, difficultySortDecimals, MidpointRounding.AwayFromZero))
                        .ThenByDescending(decision => decision.Unit.Kind)
                        .ThenBy(decision => decision.Unit.Start)
                        .First().Unit.TokenCount / (double)totalTokens;
                    if (minimumPrefixRatio > previousSelected + epsilon)
                        forcedMinimumSpanIncreases++;
                }
            }
        }

        if (selected <= epsilon)
        {
            zeroAt = encounter;
            break;
        }

        var mixedPlan = BuildLearningPlan(selectedPlan);
        var session = new LearningEncounterSession(mixedPlan, adaptation);
        foreach (var decision in selectedPlan.Decisions)
            session.Record(decision.Unit, LearningEvidenceKind.RecallSucceeded);
        session.Finish(successfulUnassistedCompletion: true);

        previousSelected = selected;
        previousTarget = target;
        previousMean = mean;
    }

    return new ProfileResult(
        observations,
        transitions,
        selectedIncreases,
        maxIncrease,
        absoluteTargetErrorSum,
        positiveOvershootSum,
        positiveOvershootObservations,
        undershootSum,
        undershootObservations,
        maxOvershoot,
        forcedMinimumSpanIncreases,
        zeroAt);
}

AssistancePlan SelectWithPenalty(AssistancePlan productionPlan, double previousSelected, double lambda, int atomicTokens)
{
    if (productionPlan.Decisions.Count == 0)
        return productionPlan;

    var priority = productionPlan.Decisions
        .OrderByDescending(decision => Math.Round(decision.Difficulty, difficultySortDecimals, MidpointRounding.AwayFromZero))
        .ThenByDescending(decision => decision.Unit.Kind)
        .ThenBy(decision => decision.Unit.Start)
        .ToArray();

    var bestLength = 1;
    var bestRatio = priority[0].Unit.TokenCount / (double)atomicTokens;
    var bestCost = Cost(bestRatio, productionPlan.TargetRatio, previousSelected, lambda);
    for (var length = 2; length <= priority.Length; length++)
    {
        var tokens = 0;
        for (var index = 0; index < length; index++)
            tokens += priority[index].Unit.TokenCount;
        var ratio = Math.Min(1.0, tokens / (double)atomicTokens);
        var cost = Cost(ratio, productionPlan.TargetRatio, previousSelected, lambda);
        var bestIncrease = double.IsNaN(previousSelected) ? 0.0 : Math.Max(0.0, bestRatio - previousSelected);
        var increase = double.IsNaN(previousSelected) ? 0.0 : Math.Max(0.0, ratio - previousSelected);

        if (cost < bestCost - epsilon ||
            (Math.Abs(cost - bestCost) <= epsilon && increase < bestIncrease - epsilon) ||
            (Math.Abs(cost - bestCost) <= epsilon && Math.Abs(increase - bestIncrease) <= epsilon && ratio > bestRatio + epsilon))
        {
            bestCost = cost;
            bestLength = length;
            bestRatio = ratio;
        }
    }

    var chosen = priority.Take(bestLength).OrderBy(decision => decision.Unit.Start).ToArray();
    return new AssistancePlan(chosen, productionPlan.TargetRatio, bestRatio);
}

double Cost(double selected, double target, double previous, double lambda)
{
    var increase = double.IsNaN(previous) ? 0.0 : Math.Max(0.0, selected - previous);
    return Math.Abs(selected - target) + lambda * increase;
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
    return new MixedLanguagePlan(source, segments, assistance, document);
}

sealed record ProfileResult(
    int Observations,
    int Transitions,
    int SelectedIncreases,
    double MaxIncrease,
    double AbsoluteTargetErrorSum,
    double PositiveOvershootSum,
    int PositiveOvershootObservations,
    double UndershootSum,
    int UndershootObservations,
    double MaxPositiveOvershoot,
    int ForcedMinimumSpanIncreases,
    int? ZeroAt);

sealed class Accumulator
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
    private double undershootSum;
    private int undershootObservations;
    private double maxPositiveOvershoot;
    private int forcedMinimumSpanIncreases;
    private int profilesReachingZero;
    private int maxZeroEncounter;

    public Accumulator(string name, double? lambda) { Name = name; Lambda = lambda; }
    public string Name { get; }
    public double? Lambda { get; }

    public void Add(ProfileResult result)
    {
        profiles++;
        observations += result.Observations;
        transitions += result.Transitions;
        if (result.SelectedIncreases > 0) profilesWithIncrease++;
        increaseTransitions += result.SelectedIncreases;
        if (result.MaxIncrease > maxIncrease) maxIncrease = result.MaxIncrease;
        absoluteTargetErrorSum += result.AbsoluteTargetErrorSum;
        positiveOvershootSum += result.PositiveOvershootSum;
        positiveOvershootObservations += result.PositiveOvershootObservations;
        undershootSum += result.UndershootSum;
        undershootObservations += result.UndershootObservations;
        if (result.MaxPositiveOvershoot > maxPositiveOvershoot) maxPositiveOvershoot = result.MaxPositiveOvershoot;
        forcedMinimumSpanIncreases += result.ForcedMinimumSpanIncreases;
        if (result.ZeroAt.HasValue)
        {
            profilesReachingZero++;
            if (result.ZeroAt.Value > maxZeroEncounter) maxZeroEncounter = result.ZeroAt.Value;
        }
    }

    public StrategyReport Build() => new StrategyReport(
        Name,
        Lambda,
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
        undershootObservations == 0 ? 0.0 : undershootSum / undershootObservations,
        undershootObservations,
        maxPositiveOvershoot,
        forcedMinimumSpanIncreases,
        profilesReachingZero,
        maxZeroEncounter);
}

sealed record StrategyReport(
    string Strategy,
    double? Lambda,
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
    double MeanUndershoot,
    int UndershootObservations,
    double MaxPositiveOvershoot,
    int ForcedMinimumSpanIncreaseTransitions,
    int ProfilesReachingZeroAssistance,
    int MaxEncountersToZero);
