using System.Text.Json;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Semantics;

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

        foreach (var decision in plan.Decisions)
        {
            if (decision.Unit.Kind != SemanticUnitKind.Word &&
                decision.Unit.Kind != SemanticUnitKind.MultiwordExpression &&
                decision.Unit.Kind != SemanticUnitKind.Clause)
            {
                throw new InvalidOperationException(
                    $"Planner selected an unsupported assistance granularity: {decision.Unit.Kind}.");
            }

            var sourceSlice = source.Substring(decision.Unit.Start, decision.Unit.Length);
            if (!string.Equals(sourceSlice, decision.Unit.Text, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Selected semantic unit no longer matches an exact source span.");
            }
        }

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

var report = new
{
    status = "pass",
    experiment = "semantic-assistance-understanding-sweep",
    scope = "core-behavior-only",
    source,
    semantic_atomicity_required = true,
    character_percentage_selection_used = false,
    sweep_points_per_mode = 101,
    understanding_min = 0.0,
    understanding_max = 1.0,
    modes = modeReports,
    representative_understanding = representativeUnderstanding,
    fixed_mode_ratios = fixedModeRatios,
    quest_execution_performed = false,
    latency_measured = false,
};

Console.WriteLine(JsonSerializer.Serialize(report));
