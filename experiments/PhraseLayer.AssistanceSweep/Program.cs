using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Translation;

const string source = "I was tired, so I went home, and I fell asleep immediately.";
const double epsilon = 1e-12;
var document = new RuleBasedSemanticSegmenter().Segment(source);
var planner = new AssistancePlanner();
var modes = new[] { AssistanceMode.Auto, AssistanceMode.Easy, AssistanceMode.Balanced, AssistanceMode.Challenge, AssistanceMode.Immersion };
var modeReports = new List<object>();
foreach (var mode in modes)
{
    var previousSelected = double.PositiveInfinity;
    var previousTarget = double.PositiveInfinity;
    var breakpoints = new List<object>();
    double? lastRecordedSelected = null;
    for (var point = 0; point <= 100; point++)
    {
        var understanding = point / 100.0;
        var plan = planner.Plan(document, new InMemoryLearnerModel(understanding), AssistancePolicy.ForMode(mode));
        if (plan.SelectedRatio > previousSelected + epsilon) throw new InvalidOperationException($"Uniform sweep support increased: mode={mode}, understanding={understanding}.");
        if (mode == AssistanceMode.Auto && plan.TargetRatio > previousTarget + epsilon) throw new InvalidOperationException("Auto target increased in the uniform sweep.");
        RequireSemanticAssistanceSpans(source, plan);
        if (lastRecordedSelected is null || Math.Abs(plan.SelectedRatio - lastRecordedSelected.Value) > epsilon)
        {
            breakpoints.Add(new { understanding, target_ratio = plan.TargetRatio, selected_ratio = plan.SelectedRatio,
                selected_units = plan.Decisions.Count, unit_kinds = plan.Decisions.Select(item => item.Unit.Kind.ToString()).Distinct().ToArray() });
            lastRecordedSelected = plan.SelectedRatio;
        }
        previousSelected = plan.SelectedRatio;
        previousTarget = plan.TargetRatio;
    }
    modeReports.Add(new { mode = mode.ToString(), monotonic_nonincreasing = true, breakpoints });
}
var representativeUnderstanding = 0.55;
var fixedModeRatios = new List<object>();
var previousModeRatio = double.PositiveInfinity;
foreach (var mode in modes.Skip(1))
{
    var plan = planner.Plan(document, new InMemoryLearnerModel(representativeUnderstanding), AssistancePolicy.ForMode(mode));
    if (plan.SelectedRatio > previousModeRatio + epsilon) throw new InvalidOperationException("Harder mode increased support.");
    fixedModeRatios.Add(new { mode = mode.ToString(), selected_ratio = plan.SelectedRatio });
    previousModeRatio = plan.SelectedRatio;
}
var passiveTrajectory = await RunEncounterTrajectoryAsync("assisted-exposure-plus-successful-unassisted-completion", false, 80);
var recallTrajectory = await RunEncounterTrajectoryAsync("recall-success-plus-successful-unassisted-completion", true, 80);
if (passiveTrajectory.FinalSelectedRatio <= epsilon) throw new InvalidOperationException("No-recall trajectory unexpectedly removed all assistance.");
if (recallTrajectory.FinalSelectedRatio > epsilon || recallTrajectory.EncountersToZero is null)
    throw new InvalidOperationException("Recall trajectory did not reach zero assistance within its budget.");
Console.WriteLine(JsonSerializer.Serialize(new
{
    status = "pass", experiment = "semantic-assistance-understanding-and-learning-sweep", scope = "deterministic-core-policy-only",
    source, semantic_atomicity_required = true, character_percentage_selection_used = false,
    sweep_points_per_mode = 101, understanding_min = 0.0, understanding_max = 1.0, modes = modeReports,
    representative_understanding = representativeUnderstanding, fixed_mode_ratios = fixedModeRatios,
    repeated_encounter_experiments = new { passive = passiveTrajectory, recall_success = recallTrajectory,
        translation_fixture = "nonempty-constant-synthetic-text", successful_unassisted_completion_assumed = true,
        human_learning_effectiveness_measured = false },
    quest_execution_performed = false, latency_measured = false,
}));

async Task<TrajectoryReport> RunEncounterTrajectoryAsync(string name, bool recordRecallSuccess, int maxEncounters)
{
    var learner = new InMemoryLearnerModel(0.20);
    var adaptation = new LearnerAdaptationEngine(learner);
    // The old empty dictionary returned source text: it did not actually provide an assisted display.
    var pipeline = new LanguagePipeline(new RuleBasedSemanticSegmenter(), learner, new AssistancePlanner(), new FixtureTranslator());
    var atomicWords = new RuleBasedSemanticSegmenter().Segment(source).OfKind(SemanticUnitKind.Word).ToArray();
    var breakpoints = new List<object>();
    double? lastRecordedRatio = null;
    var previousRatio = double.PositiveInfinity;
    int? encountersToZero = null;
    var finalSelectedRatio = double.NaN;
    for (var encounter = 1; encounter <= maxEncounters; encounter++)
    {
        var plan = await pipeline.PlanAsync(source, AssistancePolicy.ForMode(AssistanceMode.Auto));
        RequireSemanticAssistanceSpans(source, plan.Assistance);
        var selectedRatio = plan.Assistance.SelectedRatio;
        if (selectedRatio > previousRatio + epsilon) throw new InvalidOperationException($"Trajectory support increased: {name}, encounter={encounter}.");
        if (lastRecordedRatio is null || Math.Abs(selectedRatio - lastRecordedRatio.Value) > epsilon)
        {
            breakpoints.Add(new { encounter, selected_ratio = selectedRatio, target_ratio = plan.Assistance.TargetRatio,
                selected_units = plan.Assistance.Decisions.Count, mean_atomic_understanding = atomicWords.Average(word => learner.Estimate(word).Understanding),
                unit_kinds = plan.Assistance.Decisions.Select(item => item.Unit.Kind.ToString()).Distinct().ToArray() });
            lastRecordedRatio = selectedRatio;
        }
        finalSelectedRatio = selectedRatio;
        if (selectedRatio <= epsilon) { encountersToZero = encounter; break; }
        var session = new LearningEncounterSession(plan, adaptation);
        if (recordRecallSuccess)
            foreach (var decision in plan.Assistance.Decisions) session.Record(decision.Unit, LearningEvidenceKind.RecallSucceeded);
        session.Finish(successfulUnassistedCompletion: true);
        previousRatio = selectedRatio;
    }
    if (double.IsNaN(finalSelectedRatio)) throw new InvalidOperationException("No trajectory observations.");
    return new TrajectoryReport(name, recordRecallSuccess, maxEncounters, finalSelectedRatio, encountersToZero, breakpoints.ToArray());
}
static void RequireSemanticAssistanceSpans(string sourceText, AssistancePlan plan)
{
    var end = 0;
    foreach (var decision in plan.Decisions.OrderBy(item => item.Unit.Start))
    {
        var unit = decision.Unit;
        if (unit.Kind != SemanticUnitKind.Word && unit.Kind != SemanticUnitKind.MultiwordExpression && unit.Kind != SemanticUnitKind.Clause)
            throw new InvalidOperationException("Unsupported assistance granularity.");
        if (unit.Start < end || !string.Equals(sourceText.Substring(unit.Start, unit.Length), unit.Text, StringComparison.Ordinal))
            throw new InvalidOperationException("Assistance spans must be non-overlapping exact source slices.");
        end = unit.End;
    }
}
sealed record TrajectoryReport(string Name, bool RecallSuccessRecorded, int EncounterBudget, double FinalSelectedRatio, int? EncountersToZero, object[] Breakpoints);
sealed class FixtureTranslator : ITranslationEngine
{
    public Task<string> TranslateAsync(string sourceText, string context, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult("試験訳"); }
}
