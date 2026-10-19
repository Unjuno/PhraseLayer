using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Spatial;
using PhraseLayer.Unity;

var results = new List<object>();
var failures = 0;
Check("successful_presentation", () => { var f = VisibleFixture(); return f.Mask.Visible && f.Renderer.Visible; });
Check("renderer_false_clears_mask", () => {
    var f = VisibleFixture(); f.Renderer.ReturnValue = false;
    f.Tracking.Track(Layout(), 1_000_200_000);
    return !f.Mask.Visible && !f.Tracking.LastMaskSucceeded && !f.Tracking.LastRenderSucceeded;
});
Check("renderer_exception_clears_existing_mask", () => {
    var f = VisibleFixture(); f.Renderer.ThrowOnPresent = true;
    try { f.Tracking.Track(Layout(), 1_000_200_000); } catch (InvalidOperationException) { }
    return !f.Mask.Visible && !f.Tracking.LastMaskSucceeded && !f.Tracking.LastRenderSucceeded;
});
Check("absent_renderer_never_covers_source", () => {
    var tracker = new UnityWorldTextTrackingBehaviour(); var mask = new UnityWorldTextSourceMaskBehaviour();
    tracker.SetSourceMask(mask); tracker.Track(Layout(), 0); tracker.Track(Layout(), 100_000);
    return !mask.Visible && !tracker.LastMaskSucceeded;
});
Check("mask_exception_clears_previous_cover", () => {
    var f = VisibleFixture(); f.Mask.ThrowOnPresent = true;
    try { f.Tracking.Track(Layout(), 1_000_200_000); } catch (InvalidOperationException) { }
    return !f.Mask.Visible && !f.Tracking.LastMaskSucceeded;
});
Check("projection_exception_clears_previous_cover", () => {
    var f = VisibleFixture();
    try { f.Tracking.ProjectFitAndTrack(null, 1_000_200_000); } catch (ArgumentNullException) { }
    return !f.Mask.Visible && !f.Renderer.Visible;
});
Check("disable_clears_sibling_presenters", () => {
    var f = VisibleFixture(); Message(f.Tracking, "OnDisable");
    return !f.Mask.Visible && !f.Renderer.Visible && f.Tracking.LastPlan == null;
});
Check("destroy_clears_sibling_presenters", () => {
    var f = VisibleFixture(); Message(f.Tracking, "OnDestroy");
    return !f.Mask.Visible && !f.Renderer.Visible;
});
Check("renderer_reassignment_releases_old_presenter", () => {
    var f = VisibleFixture(); f.Tracking.SetRenderer(new UnityWorldTextRendererBehaviour());
    return !f.Mask.Visible && !f.Renderer.Visible;
});
Check("mask_reassignment_releases_old_cover", () => {
    var f = VisibleFixture(); f.Tracking.SetSourceMask(new UnityWorldTextSourceMaskBehaviour());
    return !f.Mask.Visible;
});
Check("silence_expires_without_new_ocr", () => {
    var f = VisibleFixture(); UnityEngine.Time.realtimeSinceStartupAsDouble = 101.0;
    Message(f.Tracking, "LateUpdate");
    return !f.Mask.Visible && !f.Renderer.Visible && f.Tracking.LastPlan == null;
});
Check("unscaled_clock_rollback_fails_closed", () => {
    var f = VisibleFixture(); UnityEngine.Time.realtimeSinceStartupAsDouble = 99.0;
    Message(f.Tracking, "LateUpdate");
    return !f.Mask.Visible;
});
Check("fresh_presentation_is_not_expired", () => {
    var f = VisibleFixture(); UnityEngine.Time.realtimeSinceStartupAsDouble = 100.1;
    Message(f.Tracking, "LateUpdate");
    return f.Mask.Visible && f.Renderer.Visible;
});
Check("reset_clears_presenters", () => {
    var f = VisibleFixture(); f.Tracking.ResetTracking(); return !f.Mask.Visible && !f.Renderer.Visible;
});
var json = JsonSerializer.Serialize(new {
    experiment = "world-presentation-failure-and-silence-audit", experiment_status = "completed",
    safety_result = failures == 0 ? "PASS" : "FAIL", failed_checks = failures, checks = results,
    scope = "production-Unity-coordinator-source-with-host-renderer-and-clock-test-doubles",
    production_tracking_source_linked = true, renderer_failure_injected = true,
    camera_timestamp_epoch_differs_from_receipt_clock = true,
    framework = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    real_unity_execution_performed = false, gpu_execution_performed = false, quest_execution_performed = false,
    human_learning_effectiveness_measured = false, latency_measured = false
});
Console.WriteLine(json);
var reportPath = Environment.GetEnvironmentVariable("AUDIT_REPORT_PATH");
if (!string.IsNullOrEmpty(reportPath)) System.IO.File.WriteAllText(reportPath, json);
return args.Contains("--require-safe") && failures != 0 ? 1 : 0;

void Check(string name, Func<bool> action) {
    bool passed; string error = null;
    try { passed = action(); } catch (Exception exception) { passed = false; error = exception.GetType().Name; }
    if (!passed) failures++;
    results.Add(new { name, passed, error_type = error });
}
static void Message(object instance, string name) {
    var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
    method?.Invoke(instance, null);
}
static Fixture VisibleFixture() {
    UnityEngine.Time.realtimeSinceStartupAsDouble = 100.0;
    var f = new Fixture(); f.Tracking.SetRenderer(f.Renderer); f.Tracking.SetSourceMask(f.Mask);
    f.Tracking.Track(Layout(), 1_000_000_000); f.Tracking.Track(Layout(), 1_000_100_000);
    if (!f.Mask.Visible || !f.Renderer.Visible) throw new InvalidOperationException("Fixture never became visible.");
    return f;
}
static WorldTextLayoutPlan Layout() {
    var unit = new SemanticUnit("mwe", SemanticUnitKind.MultiwordExpression, 0, 8, "KEEP OFF", 2);
    var segment = new MixedLanguageSegment("KEEP OFF", "立入禁止", true, unit);
    var target = new SpatialAssistanceTarget(segment, Array.Empty<OcrTextRegionSpan>(), SpatialAssistanceCoverage.Exact, new ViewportEnvelope(0.2, 0.3, 0.4, 0.5));
    var ray = new SpatialRay(new SpatialVector3(0, 0, -1), new SpatialVector3(0, 0, 1));
    var projected = new ProjectedAssistanceTarget(target, OverlayPlacementKind.InPlaceReplacement, SpatialProjectionFailure.None, new ViewportPoint(0.3, 0.4), ray, new SurfaceHit(new SpatialVector3(0, 0, 0), new SpatialVector3(0, 0, -1), 1));
    var surface = new WorldTextSurface(new SpatialVector3(0, 0, 0), new SpatialVector3(1, 0, 0), new SpatialVector3(0, 1, 0), new SpatialVector3(0, 0, 1), 0.2, 0.05, 0);
    return new WorldTextLayoutPlan(new[] { new WorldTextLayoutTarget(projected, WorldTextLayoutFailure.None, surface) });
}
sealed class Fixture {
    public readonly UnityWorldTextTrackingBehaviour Tracking = new UnityWorldTextTrackingBehaviour();
    public readonly UnityWorldTextRendererBehaviour Renderer = new UnityWorldTextRendererBehaviour();
    public readonly UnityWorldTextSourceMaskBehaviour Mask = new UnityWorldTextSourceMaskBehaviour();
}
namespace UnityEngine {
    public class MonoBehaviour { }
    [AttributeUsage(AttributeTargets.Field)] public sealed class SerializeField : Attribute { }
    public static class Time { public static double realtimeSinceStartupAsDouble; }
}
namespace PhraseLayer.Unity {
    public sealed class UnitySpatialProjectionBehaviour {
        public WorldTextLayoutPlan ProjectAndFitWorldText(ReadModeAlignedResult aligned) { throw new InvalidOperationException("injected projection failure"); }
    }
    public sealed class UnityWorldTextRendererBehaviour {
        public bool ReturnValue = true;
        public bool ThrowOnPresent;
        public bool Visible;
        public bool TryPresent(WorldTextTrackingPlan plan) {
            if (ThrowOnPresent) throw new InvalidOperationException("injected renderer failure");
            Visible = ReturnValue && plan != null && plan.Tracks.Count != 0; return ReturnValue;
        }
        public void Clear() { Visible = false; }
    }
    public sealed class UnityWorldTextSourceMaskBehaviour {
        public bool ThrowOnPresent;
        public bool Visible;
        public bool TryPresent(WorldTextTrackingPlan plan) {
            if (ThrowOnPresent) throw new InvalidOperationException("injected mask failure");
            Visible = plan != null && plan.Tracks.Any(track => new WorldTextMaskPolicy().Evaluate(track).CanMask); return true;
        }
        public void Clear() { Visible = false; }
    }
}
