using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Learning;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Spatial;
using PhraseLayer.Core.Translation;
using PhraseLayer.Unity;

var checks = new List<object>(); var failed = 0;
Check("normal_observation_is_presented", f => f.Tracking.Visible && f.Live.ProcessedObservationCount == 1);
Check("disable_removes_existing_translation", f => { Message(f.Live, "OnDisable"); return !f.Tracking.Visible && f.Live.LastAlignedResult == null; });
Check("destroy_removes_existing_translation", f => { Message(f.Live, "OnDestroy"); return !f.Tracking.Visible; });
Check("scene_reassignment_clears_old_owner", f => {
    f.Live.SetSceneReferences(new OcrViewportDebugBehaviour(), new UnityWorldTextTrackingBehaviour()); return !f.Tracking.Visible;
});
Check("pending_old_scene_result_cannot_enter_new_scene", f => {
    f.Pending(); var next = new UnityWorldTextTrackingBehaviour(); f.Live.SetSceneReferences(new OcrViewportDebugBehaviour(), next);
    f.Complete(); return !next.Visible && next.PresentCalls == 0 && !f.Tracking.Visible;
});
Check("mode_change_invalidates_pending_result", f => {
    f.Pending(); var calls = f.Tracking.PresentCalls; f.Live.SetAssistanceMode(AssistanceMode.Immersion); f.Complete();
    return !f.Tracking.Visible && f.Tracking.PresentCalls == calls;
});
Check("same_mode_does_not_restart_encounter", f => {
    f.Live.SetAssistanceMode(f.Live.AssistanceMode); return f.Tracking.Visible;
});
Check("pipeline_replacement_clears_old_translation", f => {
    f.Live.ConfigureLanguagePipeline(Fixture.Pipeline(new Translator())); return !f.Tracking.Visible;
});
Check("translation_error_clears_previous_success", f => {
    f.Pending(); f.Translator.Block.SetException(new InvalidOperationException("synthetic failure")); f.Context.Drain();
    return !f.Tracking.Visible && f.Live.LastError != null && f.Live.LastAlignedResult == null;
});
Check("invalid_mode_is_rejected_without_mutation", f => {
    var before = f.Live.AssistanceMode;
    try { f.Live.SetAssistanceMode((AssistanceMode)999); return false; }
    catch (ArgumentOutOfRangeException) { return f.Live.AssistanceMode == before; }
});
Check("new_scene_can_process_fresh_observation", f => {
    f.Pending(); var next = new UnityWorldTextTrackingBehaviour(); var presenter = new OcrViewportDebugBehaviour();
    f.Live.SetSceneReferences(presenter, next); f.Complete(); f.Translator.Block = null; presenter.Publish("fresh", 3_000_000); f.Context.Drain();
    return next.PresentCalls == 1 && next.Visible;
});
var json = JsonSerializer.Serialize(new {
    experiment = "live-read-mode-lifecycle-and-owner-generation-audit", experiment_status = "completed",
    safety_result = failed == 0 ? "PASS" : "FAIL", failed_checks = failed, checks,
    scope = "production-live-component-and-core-with-host-Unity-lifecycle-and-presentation-test-doubles",
    owner_thread_continuations_pumped = true, git_commit = Environment.GetEnvironmentVariable("GITHUB_SHA"),
    framework = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    real_unity_execution_performed = false, quest_execution_performed = false, latency_measured = false
});
Console.WriteLine(json);
var path = Environment.GetEnvironmentVariable("AUDIT_REPORT_PATH"); if (!string.IsNullOrEmpty(path)) System.IO.File.WriteAllText(path, json);
return args.Contains("--require-safe") && failed != 0 ? 1 : 0;
void Check(string name, Func<Fixture, bool> action) {
    var previous = SynchronizationContext.Current; var context = new PumpContext(); SynchronizationContext.SetSynchronizationContext(context);
    bool passed; string error = null; Fixture fixture = null;
    try { fixture = new Fixture(context); passed = action(fixture); }
    catch (Exception e) { passed = false; error = e.GetType().Name; }
    finally { if (fixture != null) { Message(fixture.Live, "OnDestroy"); fixture.Translator.Block?.TrySetResult("discarded"); context.Drain(); } SynchronizationContext.SetSynchronizationContext(previous); }
    checks.Add(new { name, passed, error_type = error }); if (!passed) failed++;
}
static void Message(object instance, string name) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(instance, null);
sealed class Fixture {
    public readonly UnityLiveReadModeBehaviour Live = new(); public readonly UnityWorldTextTrackingBehaviour Tracking = new();
    public readonly OcrViewportDebugBehaviour Presenter = new(); public readonly Translator Translator = new(); public readonly PumpContext Context;
    public Fixture(PumpContext context) {
        Context = context; Live.SetSceneReferences(Presenter, Tracking); Live.ConfigureLanguagePipeline(Pipeline(Translator));
        Live.GetType().GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Live, null);
        Presenter.Publish("hello", 1_000_000); context.Drain();
        if (!Tracking.Visible) throw new InvalidOperationException("Fixture never became visible.");
    }
    public void Pending() { Translator.Block = new TaskCompletionSource<string>(); Presenter.Publish("world", 2_000_000); Context.Drain(); }
    public void Complete() { Translator.Block.SetResult("delayed"); Context.Drain(); }
    public static LanguagePipeline Pipeline(Translator translator) => new(new RuleBasedSemanticSegmenter(), new InMemoryLearnerModel(0), new AssistancePlanner(), translator);
}
sealed class Translator : ITranslationEngine {
    public TaskCompletionSource<string> Block;
    public Task<string> TranslateAsync(string source, string context, CancellationToken token = default) => Block == null ? Task.FromResult("翻訳") : Block.Task;
}
sealed class PumpContext : SynchronizationContext {
    private readonly Queue<(SendOrPostCallback Callback, object State)> queue = new();
    public override void Post(SendOrPostCallback callback, object state) { lock (queue) queue.Enqueue((callback, state)); }
    public void Drain() {
        for (var iteration = 0; iteration < 10000; iteration++) {
            (SendOrPostCallback Callback, object State) item;
            lock (queue) { if (queue.Count == 0) return; item = queue.Dequeue(); }
            item.Callback(item.State);
        }
        throw new InvalidOperationException("Continuation pump did not settle.");
    }
}
namespace UnityEngine {
    public class MonoBehaviour { }
    [AttributeUsage(AttributeTargets.Field)] public sealed class SerializeField : Attribute { }
    public static class Debug { public static void LogException(Exception exception, object context) { } }
}
namespace PhraseLayer.Unity {
    public sealed class OcrViewportDebugBehaviour {
        public event Action<OcrObservation, ImageFrame> ObservationPresented;
        public void Publish(string text, long timestamp) => ObservationPresented?.Invoke(new OcrObservation(text, 0.99), new ImageFrame(new byte[4], 10, 10, timestamp));
    }
    public sealed class UnityWorldTextTrackingBehaviour {
        public bool Visible; public int PresentCalls;
        public WorldTextTrackingPlan ProjectFitAndTrack(ReadModeAlignedResult aligned, long timestamp) { PresentCalls++; Visible = true; return new WorldTextTrackingPlan(timestamp, Array.Empty<WorldTextTrackState>()); }
        public void ResetTracking() { Visible = false; }
    }
}
