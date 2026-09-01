using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Spatial;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Real-device vertical-slice smoke test for Read Mode.
    ///
    /// PASS requires the real Quest camera/OCR smoke to pass first, then a newer adaptive Read Mode observation must
    /// reach MRUK live-depth surface fitting/tracking and produce both a currently eligible source mask and world-space
    /// text. The report contains geometry/count diagnostics only; recognized source/translated text is not copied.
    /// </summary>
    public sealed class QuestReadModeSmokeTestBehaviour : MonoBehaviour
    {
        [SerializeField] private QuestOcrSmokeTestBehaviour ocrSmoke = default(QuestOcrSmokeTestBehaviour);
        [SerializeField] private UnityLiveReadModeBehaviour liveReadMode = default(UnityLiveReadModeBehaviour);
        [SerializeField] private UnityWorldTextTrackingBehaviour worldTextTracking = default(UnityWorldTextTrackingBehaviour);
        [SerializeField] private bool autoRunOnStart = false;
        [SerializeField] private float timeoutSeconds = 90f;
        [SerializeField] private int minimumObservedTracks = 1;
        [SerializeField] private int minimumActiveMasks = 1;
        [SerializeField] private int minimumRenderedViews = 1;
        [SerializeField] private string lastReport = string.Empty;

        private CancellationTokenSource lifetime;
        private bool isRunning;
        private bool lastPassed;
        private Exception lastError;

        public bool IsRunning => isRunning;
        public bool LastPassed => lastPassed;
        public string LastReport => lastReport;
        public Exception LastError => lastError;
        public bool AutoRunOnStart
        {
            get => autoRunOnStart;
            set => autoRunOnStart = value;
        }

        public void SetSceneReferences(
            QuestOcrSmokeTestBehaviour questOcrSmoke,
            UnityLiveReadModeBehaviour liveRuntime,
            UnityWorldTextTrackingBehaviour tracking)
        {
            ocrSmoke = questOcrSmoke ?? throw new ArgumentNullException(nameof(questOcrSmoke));
            liveReadMode = liveRuntime ?? throw new ArgumentNullException(nameof(liveRuntime));
            worldTextTracking = tracking ?? throw new ArgumentNullException(nameof(tracking));
        }

        private void OnEnable()
        {
            lifetime?.Dispose();
            lifetime = new CancellationTokenSource();
        }

        private async void Start()
        {
            if (!autoRunOnStart) return;

            try
            {
                await RunSmokeTestAsync(lifetime == null ? default(CancellationToken) : lifetime.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                lastError = exception;
                Debug.LogException(exception);
            }
        }

        private void OnDisable()
        {
            lifetime?.Cancel();
        }

        private void OnDestroy()
        {
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;
        }

        public async Task<string> RunSmokeTestAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            EnsureReferences();
            ValidateSettings();
            if (isRunning)
                throw new InvalidOperationException("Quest Read Mode smoke test is already running.");
            if (!liveReadMode.IsConfigured)
                throw new InvalidOperationException("Live Read Mode must be configured before the Quest Read Mode smoke test runs.");
            if (worldTextTracking.Renderer == null || !worldTextTracking.Renderer.IsConfigured)
                throw new InvalidOperationException("Assign a reviewed Japanese-capable Font before the Quest Read Mode smoke test runs.");
            if (worldTextTracking.SourceMask == null || !worldTextTracking.SourceMask.IsConfigured)
                throw new InvalidOperationException("Assign a reviewed opaque source-mask Material before the Quest Read Mode smoke test runs.");
            if (worldTextTracking.Projection == null || worldTextTracking.Projection.EnvironmentSurfaceRaycaster == null)
                throw new InvalidOperationException("Quest Read Mode smoke requires MRUK EnvironmentRaycastManager live-depth projection.");

            isRunning = true;
            lastPassed = false;
            lastError = null;
            lastReport = string.Empty;
            var startedAt = Time.realtimeSinceStartupAsDouble;
            var processedBefore = liveReadMode.ProcessedObservationCount;

            using (var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var runToken = timeoutCancellation.Token;
                try
                {
                    var ocrReport = await ocrSmoke.RunSmokeTestAsync(runToken);
                    while (true)
                    {
                        runToken.ThrowIfCancellationRequested();

                        var plan = worldTextTracking.LastPlan;
                        var renderer = worldTextTracking.Renderer;
                        var mask = worldTextTracking.SourceMask;
                        var projection = worldTextTracking.Projection;
                        var newerReadProcessed = liveReadMode.ProcessedObservationCount > processedBefore;
                        var enoughObservedTracks = plan != null && plan.ObservedCount >= minimumObservedTracks;
                        var enoughMasks = mask != null && mask.ActiveMaskCount >= minimumActiveMasks;
                        var enoughRenderedViews = renderer != null && renderer.ActiveViewCount >= minimumRenderedViews;
                        var fittedWorldText = projection != null &&
                            projection.LastWorldTextLayout != null &&
                            projection.LastWorldTextLayout.ReadyCount >= minimumObservedTracks;
                        var liveDepthSurface = projection != null &&
                            projection.UsesEnvironmentRaycast &&
                            projection.EnvironmentSurfaceRaycaster != null &&
                            projection.EnvironmentSurfaceRaycaster.AbiValidated;

                        if (newerReadProcessed &&
                            liveDepthSurface &&
                            fittedWorldText &&
                            enoughObservedTracks &&
                            worldTextTracking.LastMaskSucceeded &&
                            worldTextTracking.LastRenderSucceeded &&
                            enoughMasks &&
                            enoughRenderedViews)
                        {
                            lastPassed = true;
                            lastReport = BuildReport(
                                "PASS",
                                Time.realtimeSinceStartupAsDouble - startedAt,
                                ocrReport);
                            Debug.Log(lastReport);
                            return lastReport;
                        }

                        await Task.Yield();
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    lastReport = BuildReport(
                        "FAIL_TIMEOUT",
                        Time.realtimeSinceStartupAsDouble - startedAt,
                        ocrSmoke.LastReport);
                    var timeout = new TimeoutException(lastReport);
                    lastError = timeout;
                    throw timeout;
                }
                catch (Exception exception)
                {
                    lastReport = BuildReport(
                        "FAIL_EXCEPTION",
                        Time.realtimeSinceStartupAsDouble - startedAt,
                        ocrSmoke.LastReport) + "\nexception=" + exception.GetType().Name + ": " + exception.Message;
                    lastError = exception;
                    throw;
                }
                finally
                {
                    isRunning = false;
                }
            }
        }

        private string BuildReport(string status, double elapsedSeconds, string ocrReport)
        {
            var builder = new StringBuilder(1792);
            var plan = worldTextTracking == null ? null : worldTextTracking.LastPlan;
            var projection = worldTextTracking == null ? null : worldTextTracking.Projection;
            var renderer = worldTextTracking == null ? null : worldTextTracking.Renderer;
            var mask = worldTextTracking == null ? null : worldTextTracking.SourceMask;
            var environment = projection == null ? null : projection.EnvironmentSurfaceRaycaster;

            builder.AppendLine("PhraseLayer Quest Read Mode smoke test " + status);
            builder.Append("elapsed_ms=").Append((elapsedSeconds * 1000.0).ToString("F1"))
                .Append(" read_processed=").Append(liveReadMode == null ? 0 : liveReadMode.ProcessedObservationCount)
                .Append(" read_superseded=").Append(liveReadMode == null ? 0 : liveReadMode.SupersededObservationCount)
                .Append(" read_stale=").Append(liveReadMode == null ? 0 : liveReadMode.StaleObservationCount)
                .AppendLine();
            builder.Append("surface_runtime=")
                .Append(projection != null && projection.UsesEnvironmentRaycast ? "MRUKEnvironmentRaycast" : "OtherOrUnconfigured")
                .Append(" environment_abi_validated=").Append(environment != null && environment.AbiValidated ? "true" : "false")
                .Append(" last_environment_status=").Append(environment == null || string.IsNullOrEmpty(environment.LastHitStatus) ? "unobserved" : environment.LastHitStatus)
                .Append(" last_normal_confidence=").Append(environment == null || !environment.LastNormalConfidence.HasValue ? "unobserved" : environment.LastNormalConfidence.Value.ToString("F4"))
                .AppendLine();
            builder.Append("layout_ready=")
                .Append(projection == null || projection.LastWorldTextLayout == null ? 0 : projection.LastWorldTextLayout.ReadyCount)
                .Append(" layout_failed=")
                .Append(projection == null || projection.LastWorldTextLayout == null ? 0 : projection.LastWorldTextLayout.FailedCount)
                .Append(" tracks_observed=").Append(plan == null ? 0 : plan.ObservedCount)
                .Append(" tracks_retained=").Append(plan == null ? 0 : plan.RetainedCount)
                .AppendLine();
            builder.Append("mask_render_success=").Append(worldTextTracking != null && worldTextTracking.LastMaskSucceeded ? "true" : "false")
                .Append(" masks_active=").Append(mask == null ? 0 : mask.ActiveMaskCount)
                .Append(" masks_eligible=").Append(mask == null ? 0 : mask.LastEligibleMaskCount)
                .Append(" masks_suppressed=").Append(mask == null ? 0 : mask.LastSuppressedMaskCount)
                .AppendLine();
            builder.Append("text_render_success=").Append(worldTextTracking != null && worldTextTracking.LastRenderSucceeded ? "true" : "false")
                .Append(" rendered_views=").Append(renderer == null ? 0 : renderer.ActiveViewCount)
                .Append(" max_observed_planarity_error_m=").Append(MaxObservedPlanarityError(plan).ToString("F6"))
                .AppendLine();
            builder.AppendLine("recognized_text=<redacted>");
            builder.AppendLine("display_text=<redacted>");
            builder.AppendLine("ocr_stage=" + CompactOcrStatus(ocrReport));
            return builder.ToString().TrimEnd();
        }

        private static double MaxObservedPlanarityError(WorldTextTrackingPlan plan)
        {
            if (plan == null) return 0.0;
            var maximum = 0.0;
            foreach (var track in plan.Tracks)
            {
                if (!track.ObservedThisFrame) continue;
                maximum = Math.Max(maximum, track.Surface.MaxPlanarityErrorMeters);
            }
            return maximum;
        }

        private static string CompactOcrStatus(string report)
        {
            if (string.IsNullOrEmpty(report)) return "unobserved";
            if (report.IndexOf(" PASS", StringComparison.Ordinal) >= 0) return "PASS";
            if (report.IndexOf("FAIL_TIMEOUT", StringComparison.Ordinal) >= 0) return "FAIL_TIMEOUT";
            if (report.IndexOf("FAIL_CAMERA", StringComparison.Ordinal) >= 0) return "FAIL_CAMERA";
            return "FAIL_OR_INCOMPLETE";
        }

        private void ValidateSettings()
        {
            if (timeoutSeconds <= 0f || float.IsNaN(timeoutSeconds) || float.IsInfinity(timeoutSeconds))
                throw new InvalidOperationException("Quest Read Mode smoke timeoutSeconds must be finite and greater than zero.");
            if (minimumObservedTracks < 1)
                throw new InvalidOperationException("Quest Read Mode smoke minimumObservedTracks must be at least one.");
            if (minimumActiveMasks < 1)
                throw new InvalidOperationException("Quest Read Mode smoke minimumActiveMasks must be at least one.");
            if (minimumRenderedViews < 1)
                throw new InvalidOperationException("Quest Read Mode smoke minimumRenderedViews must be at least one.");
        }

        private void EnsureReferences()
        {
            if (ocrSmoke == null)
                throw new InvalidOperationException("Assign QuestOcrSmokeTestBehaviour to QuestReadModeSmokeTestBehaviour.");
            if (liveReadMode == null)
                throw new InvalidOperationException("Assign UnityLiveReadModeBehaviour to QuestReadModeSmokeTestBehaviour.");
            if (worldTextTracking == null)
                throw new InvalidOperationException("Assign UnityWorldTextTrackingBehaviour to QuestReadModeSmokeTestBehaviour.");
        }
    }
}
