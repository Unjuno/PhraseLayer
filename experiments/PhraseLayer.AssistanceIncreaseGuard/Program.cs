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

var segmenter = new RuleBasedSemanticSegmenter();
var document = segmenter.Segment(source);
var words = document.OfKind(SemanticUnitKind.Word).ToArray();
if (words.Length != 12)
    throw new InvalidOperationException("Increase-guard fixture requires exactly 12 atomic words.");
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

var configs = new int?[] { null, 0, 1, 2 };
var reports = new List<StrategyReport>();
foreach (var allowedIncreaseTokens in configs)
{
    var accumulator = new Accumulator(
        allowedIncreaseTokens.HasValue ? "LocalIncreaseGuard" : "ProductionGreedy",
        allowedIncreaseTokens);
    for (var profile = 0; profile < profileCount; profile++)
        accumulator.Add(RunProfile(profile, initialProfiles[profile], allowedIncreaseTokens));
    reports.Add(accumulator.Build());
}

var production = reports.Single(report => report.AllowedIncreaseTokens is null);
var strict = reports.Single(report => report.AllowedIncreaseTokens == 0);
if (production.SelectedRatioIncreaseTransitions == 0)
    throw new InvalidOperationException("Baseline no longer reproduces selected-ratio increases on the stress fixture.");
if (strict.SelectedRatioIncreaseTransitions >= production.SelectedRatioIncreaseTransitions)
    throw new InvalidOperationException("Strict local guard did not reduce selected-ratio increase transitions.");
if (reports.Any(report => report.ProfilesReachingZeroAssistance != profileCount))
    throw new InvalidOperationException("An increase guard prevented a positive-evidence profile from reaching zero assistance.");

Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "pass",
    experiment = "semantic-assistance-local-increase-guard",
    scope = "deterministic-core-policy-counterfactual",
    source,
    seed,
    profiles = profileCount,
    encounter_budget = encounterBudget,
    total_atomic_tokens = totalTokens,
    intervention_rule = "use production unless selected tokens would exceed previous selected tokens plus allowance",
    intervention_preserves_hardest_first_prefix = true,
    semantic_span_splitting_allowed = false,
    character_percentage_selection_used = false,
    zero_assistance_allowed_when_candidates_exist = false,
    strategies = reports,
    production_code_changed_by_counterfactual = false,
    human_learning_effectiveness_measured = false,
    quest_execution_performed = false,
}));

ProfileResult RunProfile(int profile, double[] initial, int? allowedIncreaseTokens)
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
    var increases = 0;
    var maxIncrease = 0.0;
    var absoluteTargetError = 0.0;
    var overshootSum = 0.0;
    var overshootObservations = 0;
    var undershootSum = 0.0;
    var undershootObservations = 0;
    var interventions = 0;
    var forcedIndivisibleIncreases = 0;
    int? zeroAt = null;

    for (var encounter = 1; encounter <= encounterBudget; encounter++)
    {
        var productionPlan = planner.Plan(document, learner, AssistancePolicy.ForMode(AssistanceMode.Auto));
        var selectedPlan = ApplyGuard(
            productionPlan,
            previousSelected,
            allowedIncreaseTokens,
            totalTokens,
            out var intervened,
            out var forcedIndivisibleIncrease);
        if (intervened) interventions++;
        if (forcedIndivisibleIncrease) forcedIndivisibleIncreases++;

        var selected = selectedPlan.SelectedRatio;
        var target = selectedPlan.TargetRatio;
        var mean = words.Average(word => learner.Estimate(word).Understanding);
        if (target > previousTarget + epsilon)
            throw new InvalidOperationException($"Positive evidence increased Auto target: profile={profile}, encounter={encounter}.");
        if (mean + epsilon < previousMean)
            throw new InvalidOperationException($"Positive evidence decreased mean understanding: profile={profile}, encounter={encounter}.");

        observations++;
        absoluteTargetError += Math.Abs(selected - target);
        if (selected > target + epsilon)
        {
            overshootSum += selected - target;
            overshootObservations++;
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
                increases++;
                var jump = selected - previousSelected;
                if (jump > maxIncrease) maxIncrease = jump;
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
        increases,
        maxIncrease,
        absoluteTargetError,
        overshootSum,
        overshootObservations,
        undershootSum,
        undershootObservations,
        interventions,
        forcedIndivisibleIncreases,
        zeroAt);
}

AssistancePlan ApplyGuard(
    AssistancePlan productionPlan,
    double previousSelected,
    int? allowedIncreaseTokens,
    int atomicTokens,
    out bool intervened,
    out bool forcedIndivisibleIncrease)
{
    intervened = false;
    forcedIndivisibleIncrease = false;
    if (!allowedIncreaseTokens.HasValue || double.IsNaN(previousSelected) || productionPlan.Decisions.Count == 0)
        return productionPlan;

    var previousTokens = (int)Math.Round(previousSelected * atomicTokens, MidpointRounding.AwayFromZero);
    var allowedTokens = Math.Min(atomicTokens, previousTokens + allowedIncreaseTokens.Value);
    var productionTokens = (int)Math.Round(productionPlan.SelectedRatio * atomicTokens, MidpointRounding.AwayFromZero);
    if (productionTokens <= allowedTokens)
        return productionPlan;

    intervened = true;
    var priority = productionPlan.Decisions
        .OrderByDescending(decision => Math.Round(decision.Difficulty, difficultySortDecimals, MidpointRounding.AwayFromZero))
        .ThenByDescending(decision => decision.Unit.Kind)
        .ThenBy(decision => decision.Unit.Start)
        .ToArray();

    var cumulativeTokens = 0;
    var bestLength = 0;
    for (var index = 0; index < priority.Length; index++)
    {
        cumulativeTokens += priority[index].Unit.TokenCount;
        if (cumulativeTokens <= allowedTokens)
            bestLength = index + 1;
        else
            break;
    }

    if (bestLength == 0)
    {
        // Do not hide every candidate simply to satisfy a numerical cap. The hardest indivisible semantic unit wins.
        bestLength = 1;
        forcedIndivisibleIncrease = priority[0].Unit.TokenCount > previousTokens + allowedIncreaseTokens.Value;
    }

    var chosen = priority.Take(bestLength).OrderBy(decision => decision.Unit.Start).ToArray();
    var chosenTokens = chosen.Sum(decision => decision.Unit.TokenCount);
    return new AssistancePlan(
        chosen,
        productionPlan.TargetRatio,
        Math.Min(1.0, chosenTokens / (double)atomicTokens));
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
    int Increases,
    double MaxIncrease,
    double AbsoluteTargetError,
    double OvershootSum,
    int OvershootObservations,
    double UndershootSum,
    int UndershootObservations,
    int Interventions,
    int ForcedIndivisibleIncreases,
    int? ZeroAt);

sealed class Accumulator
{
    private int profiles;
    private int observations;
    private int transitions;
    private int profilesWithIncrease;
    private int increases;
    private double maxIncrease;
    private double absoluteTargetError;
    private double overshootSum;
    private int overshootObservations;
    private double undershootSum;
    private int undershootObservations;
    private int interventions;
    private int forcedIndivisibleIncreases;
    private int profilesReachingZero;
    private int maxZeroEncounter;

    public Accumulator(string strategy, int? allowedIncreaseTokens)
    {
        Strategy = strategy;
        AllowedIncreaseTokens = allowedIncreaseTokens;
    }

    public string Strategy { get; }
    public int? AllowedIncreaseTokens { get; }

    public void Add(ProfileResult result)
    {
        profiles++;
        observations += result.Observations;
        transitions += result.Transitions;
        if (result.Increases > 0) profilesWithIncrease++;
        increases += result.Increases;
        if (result.MaxIncrease > maxIncrease) maxIncrease = result.MaxIncrease;
        absoluteTargetError += result.AbsoluteTargetError;
        overshootSum += result.OvershootSum;
        overshootObservations += result.OvershootObservations;
        undershootSum += result.UndershootSum;
        undershootObservations += result.UndershootObservations;
        interventions += result.Interventions;
        forcedIndivisibleIncreases += result.ForcedIndivisibleIncreases;
        if (result.ZeroAt.HasValue)
        {
            profilesReachingZero++;
            if (result.ZeroAt.Value > maxZeroEncounter) maxZeroEncounter = result.ZeroAt.Value;
        }
    }

    public StrategyReport Build() => new StrategyReport(
        Strategy,
        AllowedIncreaseTokens,
        profiles,
        observations,
        transitions,
        profilesWithIncrease,
        increases,
        transitions == 0 ? 0.0 : increases / (double)transitions,
        maxIncrease,
        observations == 0 ? 0.0 : absoluteTargetError / observations,
        overshootObservations == 0 ? 0.0 : overshootSum / overshootObservations,
        overshootObservations,
        undershootObservations == 0 ? 0.0 : undershootSum / undershootObservations,
        undershootObservations,
        interventions,
        forcedIndivisibleIncreases,
        profilesReachingZero,
        maxZeroEncounter);
}

sealed record StrategyReport(
    string Strategy,
    int? AllowedIncreaseTokens,
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
    int GuardInterventions,
    int ForcedIndivisibleIncreaseTransitions,
    int ProfilesReachingZeroAssistance,
    int MaxEncountersToZero);
