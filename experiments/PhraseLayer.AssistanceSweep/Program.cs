using System.Text.Json;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Translation;

const string source = "I was tired, so I went home, and I fell asleep immediately.";
const double epsilon = 1e-12;

var document = new RuleBasedSemanticSegmenter().Segment(source);
var planner = new AssistancePlanner();
var modes = new[]
{
    AssistanceMode.Auto,
    AssistanceMode.Easy,
    AssistanceMode.Balanced,
    AssistanceMode.Challenge,
    AssistanceMode.Immersion,
};

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
        var learner = new InMemoryLearnerModel(understanding);
        var plan = planner.Plan(document, learner, AssistancePolicy.ForMode(mode));

        if (plan.SelectedRatio > previousSelected + epsilon)
        {
            throw new InvalidOperationException(
                $"Assistance ratio increased as understanding rose: mode={mode}, understanding={understanding:F2}, " +
                $"previous={previousSelected:F6}, current={plan.SelectedRatio:F6}.");
        }

        if (mode == AssistanceMode.Auto && plan.TargetRatio > previousTarget + epsilon)
        {
            throw new InvalidOperationException(
                $"Auto target ratio increased as understanding rose: understanding={understanding:F2}, " +
                $"previous={previousTarget:F6}, current={plan.TargetRatio:F6}.");
        }

        RequireSemanticAssistanceSpans(source, plan);

        if (lastRecordedSelected is null || Math.Abs(plan.SelectedRatio - lastRecordedSelected.Value) > epsilon)
        {
            breakpoints.Add(new
            {
                understanding,
                target_ratio = plan.TargetRatio,
                selected_ratio = plan.SelectedRatio,
                selected_units = plan.Decisions.Count,
                unit_kinds = plan.Decisions.Select(decision => decision.Unit.Kind.ToString()).Distinct().ToArray(),
            });
            lastRecordedSelected = plan.SelectedRatio;
        }

        previousSelected = plan.SelectedRatio;
        previousTarget = plan.TargetRatio;
    }

    modeReports.Add(new
    {
        mode = mode.ToString(),
        monotonic_nonincreasing = true,
        breakpoints,
    });
}

var representativeUnderstanding = 0.55;
var fixedModeRatios = new List<object>();
var previousModeRatio = double.PositiveInfinity;
foreach (var mode in new[] { AssistanceMode.Easy, AssistanceMode.Balanced, AssistanceMode.Challenge, AssistanceMode.Immersion })
{
    var plan = planner.Plan(
        document,
        new InMemoryLearnerModel(representativeUnderstanding),
        AssistancePolicy.ForMode(mode));
    if (plan.SelectedRatio > previousModeRatio + epsilon)
    {
        throw new InvalidOperationException(
            $"Harder assistance mode increased support: mode={mode}, selected={plan.SelectedRatio:F6}, previous={previousModeRatio:F6}.");
    }
    fixedModeRatios.Add(new { mode = mode.ToString(), selected_ratio = plan.SelectedRatio });
    previousModeRatio = plan.SelectedRatio;
}

var passiveTrajectory = await RunEncounterTrajectoryAsync(
    "passive-assisted-exposure",
    recordRecallSuccess: false,
    maxEncounters: 80);
var recallTrajectory = await RunEncounterTrajectoryAsync(
    "recall-success-on-assisted-units",
    recordRecallSuccess: true,
    maxEncounters: 80);

if (passiveTrajectory.FinalSelectedRatio <= epsilon)
{
    throw new InvalidOperationException(
        "Passive translated exposure unexpectedly removed all assistance. The reviewed policy intentionally caps passive exposure below the Known threshold.");
}
if (recallTrajectory.FinalSelectedRatio > epsilon)
{
    throw new InvalidOperationException(
        "Repeated successful recall evidence did not eventually remove assistance within the reviewed encounter budget.");
}
if (recallTrajectory.EncountersToZero is null)
{
    throw new InvalidOperationException("Recall-success trajectory reached zero assistance without recording the transition encounter.");
}

var report = new
{
    status = "pass",
    experiment = "semantic-assistance-understanding-and-learning-sweep",
    scope = "deterministic-core-policy-only",
    source,
    semantic_atomicity_required = true,
    character_percentage_selection_used = false,
    sweep_points_per_mode = 101,
    understanding_min = 0.0,
    understanding_max = 1.0,
    modes = modeReports,
    representative_understanding = representativeUnderstanding,
    fixed_mode_ratios = fixedModeRatios,
    repeated_encounter_experiments = new
    {
        passive = passiveTrajectory,
        recall_success = recallTrajectory,
        human_learning_effectiveness_measured = false,
    },
    quest_execution_performed = false,
    latency_measured = false,
};

Console.WriteLine(JsonSerializer.Serialize(report));

async Task<TrajectoryReport> RunEncounterTrajectoryAsync(
    string name,
    bool recordRecallSuccess,
    int maxEncounters)
{
    var learner = new InMemoryLearnerModel(0.20);
    var adaptation = new LearnerAdaptationEngine(learner);
    var pipeline = new LanguagePipeline(
        new RuleBasedSemanticSegmenter(),
        learner,
        new AssistancePlanner(),
        new DictionaryTranslationEngine(new Dictionary<string, string>()));
    var atomicDocument = new RuleBasedSemanticSegmenter().Segment(source);
    var atomicWords = atomicDocument.OfKind(SemanticUnitKind.Word).ToArray();
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
        if (selectedRatio > previousRatio + epsilon)
        {
            throw new InvalidOperationException(
                $"Repeated learning trajectory increased assistance: trajectory={name}, encounter={encounter}, " +
                $"previous={previousRatio:F6}, current={selectedRatio:F6}.");
        }

        var meanAtomicUnderstanding = atomicWords.Average(word => learner.Estimate(word).Understanding);
        if (lastRecordedRatio is null || Math.Abs(selectedRatio - lastRecordedRatio.Value) > epsilon)
        {
            breakpoints.Add(new
            {
                encounter,
                selected_ratio = selectedRatio,
                target_ratio = plan.Assistance.TargetRatio,
                selected_units = plan.Assistance.Decisions.Count,
                mean_atomic_understanding = meanAtomicUnderstanding,
                unit_kinds = plan.Assistance.Decisions.Select(decision => decision.Unit.Kind.ToString()).Distinct().ToArray(),
            });
            lastRecordedRatio = selectedRatio;
        }

        finalSelectedRatio = selectedRatio;
        if (selectedRatio <= epsilon)
        {
            encountersToZero ??= encounter;
            break;
        }

        var session = new LearningEncounterSession(plan, adaptation);
        if (recordRecallSuccess)
        {
            foreach (var decision in plan.Assistance.Decisions)
                session.Record(decision.Unit, LearningEvidenceKind.RecallSucceeded);
        }
        session.Finish(successfulUnassistedCompletion: true);
        previousRatio = selectedRatio;
    }

    if (double.IsNaN(finalSelectedRatio))
        throw new InvalidOperationException("Encounter trajectory did not execute.");

    return new TrajectoryReport(
        name,
        recordRecallSuccess,
        maxEncounters,
        finalSelectedRatio,
        encountersToZero,
        breakpoints.ToArray());
}

static void RequireSemanticAssistanceSpans(string sourceText, AssistancePlan plan)
{
    foreach (var decision in plan.Decisions)
    {
        if (decision.Unit.Kind != SemanticUnitKind.Word &&
            decision.Unit.Kind != SemanticUnitKind.MultiwordExpression &&
            decision.Unit.Kind != SemanticUnitKind.Clause)
        {
            throw new InvalidOperationException(
                $"Planner selected an unsupported assistance granularity: {decision.Unit.Kind}.");
        }

        var sourceSlice = sourceText.Substring(decision.Unit.Start, decision.Unit.Length);
        if (!string.Equals(sourceSlice, decision.Unit.Text, StringComparison.Ordinal))
            throw new InvalidOperationException("Selected semantic unit no longer matches an exact source span.");
    }
}

sealed record TrajectoryReport(
    string Name,
    bool RecallSuccessRecorded,
    int EncounterBudget,
    double FinalSelectedRatio,
    int? EncountersToZero,
    object[] Breakpoints);
