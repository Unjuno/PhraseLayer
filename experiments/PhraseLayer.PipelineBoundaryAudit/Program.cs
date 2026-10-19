using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Translation;

var checks = new List<object>(); var failures = 0;
foreach (var source in new[] { "", "KEEP OFF", "alpha beta gamma" }) {
    foreach (var understanding in new[] { 0.10, 0.95 }) {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var translator = new Translator();
        var pipeline = Pipeline(understanding, translator);
        var cancelled = await Cancelled(() => pipeline.PlanAsync(source, AssistancePolicy.ForMode(AssistanceMode.Balanced), cancellationToken: cts.Token));
        Add("pre_cancel_" + source.Length + "_" + understanding, cancelled && translator.Calls == 0);
    }
}
using (var cts = new CancellationTokenSource()) {
    var translator = new Translator { OnCall = () => cts.Cancel() };
    var cancelled = await Cancelled(() => Pipeline(0.55, translator).PlanAsync("alpha beta gamma", AssistancePolicy.ForMode(AssistanceMode.Easy), cancellationToken: cts.Token));
    Add("cancel_during_first_translation_stops_following_spans", cancelled && translator.Calls == 1);
}
using (var cts = new CancellationTokenSource()) {
    var runtime = new Runtime(() => cts.Cancel());
    Add("cancel_inside_offline_runtime_is_not_success", await Cancelled(() => new OfflineTranslationEngine(runtime).TranslateAsync("alpha", "alpha", cts.Token)));
}
var unit = new SemanticUnit("word", SemanticUnitKind.Word, 0, 5, "alpha", 1);
var segment = new MixedLanguageSegment("alpha", "アルファ", true, unit);
var segments = new List<MixedLanguageSegment> { segment };
var decisions = new List<AssistanceDecision> { new AssistanceDecision(unit, 0.1) };
var assistance = new AssistancePlan(decisions, 0.5, 1);
var document = new SemanticDocument("alpha", new[] { unit });
var plan = new MixedLanguagePlan("alpha", segments, assistance, document);
segments[0] = new MixedLanguageSegment("alpha", "CHANGED", false, unit);
Add("caller_list_cannot_change_frozen_display", plan.DisplayText == "アルファ");
decisions.Clear();
Add("caller_list_cannot_change_frozen_decisions", plan.Assistance.Decisions.Count == 1);
Add("exposed_segments_are_read_only", RejectMutation(plan.Segments, segment));
Add("exposed_decisions_are_read_only", RejectMutation(new AssistancePlanner().Plan(new RuleBasedSemanticSegmenter().Segment("alpha beta"), new InMemoryLearnerModel(0.1), AssistancePolicy.ForMode(AssistanceMode.Easy)).Decisions, new AssistanceDecision(unit, 0.1)));
Add("exposed_semantic_units_are_read_only", RejectMutation(document.Units, unit));
var json = JsonSerializer.Serialize(new {
    experiment = "pipeline-cancellation-and-frozen-plan-boundary-audit", experiment_status = "completed",
    safety_result = failures == 0 ? "PASS" : "FAIL", failed_checks = failures, checks,
    scope = "production-core-with-cancellation-ignoring-translation-test-doubles",
    framework = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    real_unity_execution_performed = false, quest_execution_performed = false,
    human_learning_effectiveness_measured = false, latency_measured = false
});
Console.WriteLine(json);
var reportPath = Environment.GetEnvironmentVariable("AUDIT_REPORT_PATH");
if (!string.IsNullOrEmpty(reportPath)) System.IO.File.WriteAllText(reportPath, json);
return args.Contains("--require-safe") && failures != 0 ? 1 : 0;
void Add(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) failures++; }
static LanguagePipeline Pipeline(double score, Translator translator) => new LanguagePipeline(new RuleBasedSemanticSegmenter(new[] { "keep off" }), new InMemoryLearnerModel(score), new AssistancePlanner(), translator);
static async Task<bool> Cancelled(Func<Task> action) { try { await action(); return false; } catch (OperationCanceledException) { return true; } }
static bool RejectMutation<T>(IReadOnlyList<T> values, T replacement) {
    if (values is not IList<T> mutable) return true;
    try { mutable[0] = replacement; return false; } catch (NotSupportedException) { return true; }
}
sealed class Translator : ITranslationEngine {
    public int Calls; public Action? OnCall;
    public Task<string> TranslateAsync(string sourceText, string context, CancellationToken cancellationToken = default) {
        Calls++; OnCall?.Invoke(); return Task.FromResult("翻訳");
    }
}
sealed class Runtime : IOfflineTranslationRuntime {
    private readonly Action action;
    public Runtime(Action action) { this.action = action; }
    public Task<OfflineTranslationResult> TranslateAsync(OfflineTranslationRequest request, CancellationToken cancellationToken = default) {
        action(); return Task.FromResult(new OfflineTranslationResult("翻訳", 1, 1, false, TranslationGenerationStopReason.EndOfSequence));
    }
}
