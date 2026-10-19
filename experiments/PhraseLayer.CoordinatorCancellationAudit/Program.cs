using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Translation;

var checks = new List<object>(); var failed = 0;
foreach (var operation in new[] { "supersede", "cancel", "reset", "dispose" })
    await Check("pending_token_survives_" + operation, () => TokenOwnership(operation));
await Check("reentrant_cancel_does_not_dispose_new_request_token", async () => {
    var translator = new BlockingTranslator(); using var coordinator = Create(translator);
    translator.OnCancellation = () => coordinator.CancelActive();
    var first = Submit(coordinator, 1); await translator.Entered.Task;
    LiveReadModeProcessingResult second;
    try { second = await Submit(coordinator, 2); }
    finally { translator.Release.TrySetResult("old"); }
    var old = await first.WaitAsync(TimeSpan.FromSeconds(10));
    return second.Status == LiveReadModeProcessingStatus.Superseded && old.Status == LiveReadModeProcessingStatus.Superseded;
});
await Check("throwing_callback_does_not_strand_new_request", async () => {
    var translator = new BlockingTranslator(); using var coordinator = Create(translator);
    translator.OnCancellation = () => throw new InvalidOperationException("injected cancellation callback error");
    var first = Submit(coordinator, 1); await translator.Entered.Task;
    LiveReadModeProcessingResult second;
    try { second = await Submit(coordinator, 2); }
    finally { translator.Release.TrySetResult("old"); }
    var old = await first.WaitAsync(TimeSpan.FromSeconds(10));
    var diagnostics = coordinator.GetType().GetProperty("CancellationCallbackFailureCount")?.GetValue(coordinator);
    return second.WasProcessed && old.Status == LiveReadModeProcessingStatus.Superseded && diagnostics is long count && count == 1;
});
await Check("external_cancellation_remains_cancellation", async () => {
    var translator = new BlockingTranslator(); using var coordinator = Create(translator); using var external = new CancellationTokenSource();
    var task = coordinator.SubmitAsync(Frame(1), new OcrObservation("hello", 0.99), Policy(), external.Token);
    await translator.Entered.Task; external.Cancel(); translator.Release.TrySetResult("old");
    try { await task.WaitAsync(TimeSpan.FromSeconds(10)); return false; }
    catch (OperationCanceledException) { return translator.TokenWaitHandleAccessible; }
});
await Check("128_reverse_completions_publish_only_latest", async () => {
    var translator = new AllBlockedTranslator(); using var coordinator = Create(translator);
    var tasks = new List<Task<LiveReadModeProcessingResult>>();
    for (var index = 0; index < 128; index++) tasks.Add(Submit(coordinator, index + 1));
    for (var index = 127; index >= 0; index--) translator.Releases[index].SetResult("translated");
    var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
    return results.Count(result => result.WasProcessed) == 1 && results[127].WasProcessed &&
        results.Take(127).All(result => result.Status == LiveReadModeProcessingStatus.Superseded) && translator.AccessibleTokens == 128;
});
var json = JsonSerializer.Serialize(new {
    experiment = "cancellation-source-ownership-reentrancy-and-completion-order", experiment_status = "completed",
    safety_result = failed == 0 ? "PASS" : "FAIL", failed_checks = failed, checks,
    scope = "production-core-deterministic-task-completion-and-callback-fault-injection",
    race_control = "explicit TaskCompletionSource barriers, no timing sleeps", reverse_completion_tasks = 128,
    git_commit = Environment.GetEnvironmentVariable("GITHUB_SHA"), framework = RuntimeInformation.FrameworkDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(), real_unity_execution_performed = false,
    quest_execution_performed = false, latency_measured = false
});
Console.WriteLine(json);
var path = Environment.GetEnvironmentVariable("AUDIT_REPORT_PATH"); if (!string.IsNullOrEmpty(path)) System.IO.File.WriteAllText(path, json);
return args.Contains("--require-safe") && failed != 0 ? 1 : 0;
async Task Check(string name, Func<Task<bool>> action) {
    bool passed; string? error = null;
    try { passed = await action().WaitAsync(TimeSpan.FromSeconds(15)); } catch (Exception e) { passed = false; error = e.GetType().Name; }
    checks.Add(new { name, passed, error_type = error }); if (!passed) failed++;
}
static async Task<bool> TokenOwnership(string operation) {
    var translator = new BlockingTranslator(); using var coordinator = Create(translator);
    var first = Submit(coordinator, 1); await translator.Entered.Task;
    try {
        switch (operation) {
            case "supersede": await Submit(coordinator, 2); break;
            case "cancel": coordinator.CancelActive(); break;
            case "reset": coordinator.Reset(); break;
            case "dispose": coordinator.Dispose(); break;
        }
    } finally { translator.Release.TrySetResult("old"); }
    var result = await first.WaitAsync(TimeSpan.FromSeconds(10));
    return translator.TokenWaitHandleAccessible && result.Status == LiveReadModeProcessingStatus.Superseded;
}
static LiveReadModeCoordinator Create(ITranslationEngine translator) => new LiveReadModeCoordinator(new ReadModeObservationProcessor(new LanguagePipeline(new RuleBasedSemanticSegmenter(), new InMemoryLearnerModel(0), new AssistancePlanner(), translator)));
static ImageFrame Frame(long timestamp) => new ImageFrame(new byte[4], 10, 10, timestamp);
static AssistancePolicy Policy() => AssistancePolicy.ForMode(AssistanceMode.Easy);
static Task<LiveReadModeProcessingResult> Submit(LiveReadModeCoordinator coordinator, long timestamp) => coordinator.SubmitAsync(Frame(timestamp), new OcrObservation("hello", 0.99), Policy());
sealed class BlockingTranslator : ITranslationEngine {
    public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource<string> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action? OnCancellation; public bool TokenWaitHandleAccessible; private int calls;
    public async Task<string> TranslateAsync(string source, string context, CancellationToken token = default) {
        if (Interlocked.Increment(ref calls) != 1) return "new";
        using var registration = token.Register(() => OnCancellation?.Invoke());
        Entered.TrySetResult(true); var result = await Release.Task;
        // This operation still owns the token. Do not dispose the token source until it exits.
        token.WaitHandle.WaitOne(0); TokenWaitHandleAccessible = true; return result;
    }
}
sealed class AllBlockedTranslator : ITranslationEngine {
    public readonly List<TaskCompletionSource<string>> Releases = new(); public int AccessibleTokens;
    public async Task<string> TranslateAsync(string source, string context, CancellationToken token = default) {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); Releases.Add(release);
        var value = await release.Task; token.WaitHandle.WaitOne(0); Interlocked.Increment(ref AccessibleTokens); return value;
    }
}
