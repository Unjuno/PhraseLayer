using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Translation;

const string source = "alpha beta gamma, delta epsilon zeta eta, theta iota kappa lambda mu.";
const int profileCount = 256;
const int encounterBudget = 20;
const int seed = 0x51A57E55;
const double epsilon = 1e-12;

var segmenter = new RuleBasedSemanticSegmenter();
var document = segmenter.Segment(source);
var words = document.OfKind(SemanticUnitKind.Word).ToArray();
if (words.Length != 12 || words.Select(word => word.Text).Distinct(StringComparer.OrdinalIgnoreCase).Count() != words.Length)
    throw new InvalidOperationException("Heterogeneous stress fixture requires exactly 12 unique atomic words.");

var random = new Random(seed);
var profilesWithSelectedIncrease = 0;
var selectedIncreaseTransitions = 0;
var maxSelectedIncrease = 0.0;
var examples = new List<IncreaseExample>();
var totalTransitions = 0;
var profilesReachingZero = 0;
var maxEncountersToZero = 0;

for (var profile = 0; profile < profileCount; profile++)
{
    var learner = new InMemoryLearnerModel(0.20);
    foreach (var word in words)
        learner.SetUnderstanding(word.Text, 0.05 + random.NextDouble() * 0.75);

    var adaptation = new LearnerAdaptationEngine(learner);
    var pipeline = new LanguagePipeline(
        segmenter,
        learner,
        new AssistancePlanner(),
        new DictionaryTranslationEngine(new Dictionary<string, string>()));

    var previousSelected = double.NaN;
    var previousTarget = double.PositiveInfinity;
    var previousMean = words.Average(word => learner.Estimate(word).Understanding);
    var profileHadIncrease = false;

    for (var encounter = 1; encounter <= encounterBudget; encounter++)
    {
        var plan = await pipeline.PlanAsync(source, AssistancePolicy.ForMode(AssistanceMode.Auto));
        var selected = plan.Assistance.SelectedRatio;
        var target = plan.Assistance.TargetRatio;
        var mean = words.Average(word => learner.Estimate(word).Understanding);

        if (target > previousTarget + epsilon)
        {
            throw new InvalidOperationException(
                $"Auto target increased under positive-only evidence: profile={profile}, encounter={encounter}, " +
                $"previous={previousTarget:F9}, current={target:F9}.");
        }
        if (mean + epsilon < previousMean)
        {
            throw new InvalidOperationException(
                $"Mean atomic understanding decreased under positive-only evidence: profile={profile}, encounter={encounter}, " +
                $"previous={previousMean:F9}, current={mean:F9}.");
        }

        if (!double.IsNaN(previousSelected))
        {
            totalTransitions++;
            if (selected > previousSelected + epsilon)
            {
                var jump = selected - previousSelected;
                selectedIncreaseTransitions++;
                profileHadIncrease = true;
                if (jump > maxSelectedIncrease)
                    maxSelectedIncrease = jump;
                if (examples.Count < 8)
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
                        plan.Assistance.Decisions.Select(decision => decision.Unit.Text).ToArray()));
                }
            }
        }

        if (selected <= epsilon)
        {
            profilesReachingZero++;
            if (encounter > maxEncountersToZero)
                maxEncountersToZero = encounter;
            break;
        }

        var session = new LearningEncounterSession(plan, adaptation);
        foreach (var decision in plan.Assistance.Decisions)
            session.Record(decision.Unit, LearningEvidenceKind.RecallSucceeded);
        session.Finish(successfulUnassistedCompletion: true);

        previousSelected = selected;
        previousTarget = target;
        previousMean = mean;
    }

    if (profileHadIncrease)
        profilesWithSelectedIncrease++;
}

var report = new
{
    status = "pass",
    experiment = "heterogeneous-positive-evidence-assistance-stress",
    scope = "deterministic-core-policy-only",
    source,
    seed,
    profiles = profileCount,
    encounter_budget = encounterBudget,
    total_observed_transitions = totalTransitions,
    profiles_with_selected_ratio_increase = profilesWithSelectedIncrease,
    selected_ratio_increase_transitions = selectedIncreaseTransitions,
    selected_ratio_increase_rate = totalTransitions == 0 ? 0.0 : selectedIncreaseTransitions / (double)totalTransitions,
    max_selected_ratio_increase = maxSelectedIncrease,
    profiles_reaching_zero_assistance = profilesReachingZero,
    max_encounters_to_zero = maxEncountersToZero,
    target_ratio_monotonic_nonincreasing_required = true,
    mean_atomic_understanding_monotonic_nondecreasing_required = true,
    selected_ratio_monotonic_nonincreasing_required = false,
    selected_ratio_can_overshoot_target_due_to_semantic_span_atomicity = true,
    sample_increases = examples,
    human_learning_effectiveness_measured = false,
    quest_execution_performed = false,
};

Console.WriteLine(JsonSerializer.Serialize(report));

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
