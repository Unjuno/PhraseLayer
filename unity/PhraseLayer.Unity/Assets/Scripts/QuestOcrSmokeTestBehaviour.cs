using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Inputs;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Quest-device smoke test for the real passthrough camera -> OCR path.
    ///
    /// PASS requires at least one presented OCR region from a real camera frame. The presenter synthetic fixture
    /// is disabled/cleared before the run, and the normal auto-run loop is temporarily paused so only this harness
    /// drives the OCR pump. Recognized text is omitted from diagnostics by default.
    /// </summary>
    public sealed class QuestOcrSmokeTestBehaviour : MonoBehaviour
    {
        [SerializeField] private OcrDebugRuntimeBehaviour runtimeDriver = default(OcrDebugRuntimeBehaviour);
        [SerializeField] private OcrViewportDebugBehaviour presenter = default(OcrViewportDebugBehaviour);
        [SerializeField] private UnityPaddleOcrBootstrapBehaviour bootstrap = default(UnityPaddleOcrBootstrapBehaviour);
        [SerializeField] private bool autoRunOnStart = false;
        [SerializeField] private float timeoutSeconds = 60f;
        [SerializeField] private float retryIntervalSeconds = 0.25f;
        [SerializeField] private int minimumRecognizedRegions = 1;
        [SerializeField] private bool includeRecognizedTextInReport = false;
        [SerializeField] private string lastReport = string.Empty;

        private CancellationTokenSource lifetime;
        private bool isRunning;
        private bool lastPassed;
        private Exception lastError;

        public bool IsRunning => isRunning;
        public bool LastPassed => lastPassed;
        public string LastReport => lastReport;
        public Exception LastError => lastError;

        public void SetSceneReferences(
            OcrDebugRuntimeBehaviour runtime,
            OcrViewportDebugBehaviour observationPresenter,
            UnityPaddleOcrBootstrapBehaviour ocrBootstrap)
        {
            runtimeDriver = runtime ?? throw new ArgumentNullException(nameof(runtime));
            presenter = observationPresenter ?? throw new ArgumentNullException(nameof(observationPresenter));
            bootstrap = ocrBootstrap ?? throw new ArgumentNullException(nameof(ocrBootstrap));
            presenter.LoadSyntheticFixtureOnStart = false;
            presenter.Clear();
        }

        private void Awake()
        {
            EnsureReferences();
            presenter.LoadSyntheticFixtureOnStart = false;
            presenter.Clear();
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
                throw new InvalidOperationException("Quest OCR smoke test is already running.");
            if (!bootstrap.IsInitialized)
                throw new InvalidOperationException("PP-OCR bootstrap must be initialized before the Quest OCR smoke test runs.");
            if (!runtimeDriver.IsConfigured)
                throw new InvalidOperationException("OCR runtime driver must be configured before the Quest OCR smoke test runs.");

            isRunning = true;
            lastPassed = false;
            lastError = null;

            var previousAutoRun = runtimeDriver.AutoRun;
            runtimeDriver.AutoRun = false;
            presenter.LoadSyntheticFixtureOnStart = false;
            presenter.Clear();

            var startedAt = Time.realtimeSinceStartupAsDouble;
            var attempts = 0;
            var lastAttemptMilliseconds = 0.0;
            OcrPumpResult lastResult = null;

            using (var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var runToken = timeoutCancellation.Token;

                try
                {
                    while (true)
                    {
                        runToken.ThrowIfCancellationRequested();
                        var elapsedSeconds = Time.realtimeSinceStartupAsDouble - startedAt;
                        if (elapsedSeconds >= timeoutSeconds)
                            throw new OperationCanceledException(runToken);

                        while (runtimeDriver.IsRunning)
                        {
                            runToken.ThrowIfCancellationRequested();
                            await Task.Yield();
                        }

                        attempts++;
                        var attemptStartedAt = Time.realtimeSinceStartupAsDouble;
                        try
                        {
                            lastResult = await runtimeDriver.RunOnceAsync(runToken);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            var totalMilliseconds = (Time.realtimeSinceStartupAsDouble - startedAt) * 1000.0;
                            lastAttemptMilliseconds = (Time.realtimeSinceStartupAsDouble - attemptStartedAt) * 1000.0;
                            lastReport = BuildReport(
                                "FAIL_EXCEPTION",
                                attempts,
                                totalMilliseconds,
                                lastAttemptMilliseconds,
                                lastResult) + "\nexception=" + exception.GetType().Name + ": " + exception.Message;
                            lastError = exception;
                            throw;
                        }

                        lastAttemptMilliseconds =
                            (Time.realtimeSinceStartupAsDouble - attemptStartedAt) * 1000.0;

                        if (lastResult.CameraState == CameraCaptureState.Failed)
                        {
                            var totalMilliseconds = (Time.realtimeSinceStartupAsDouble - startedAt) * 1000.0;
                            lastReport = BuildReport(
                                "FAIL_CAMERA",
                                attempts,
                                totalMilliseconds,
                                lastAttemptMilliseconds,
                                lastResult);
                            var cameraFailure = new InvalidOperationException(lastReport);
                            lastError = cameraFailure;
                            throw cameraFailure;
                        }

                        var enoughRegions =
                            lastResult.Presented &&
                            presenter.HasObservation &&
                            presenter.Regions.Count >= minimumRecognizedRegions;
                        var recognizerObserved =
                            bootstrap.RuntimeContractReport.IndexOf(
                                "recognizer=unobserved",
                                StringComparison.Ordinal) < 0;

                        if (enoughRegions && recognizerObserved)
                        {
                            var totalMilliseconds = (Time.realtimeSinceStartupAsDouble - startedAt) * 1000.0;
                            lastPassed = true;
                            lastReport = BuildReport(
                                "PASS",
                                attempts,
                                totalMilliseconds,
                                lastAttemptMilliseconds,
                                lastResult);
                            Debug.Log(lastReport);
                            return lastReport;
                        }

                        var retryAt = Time.realtimeSinceStartupAsDouble + retryIntervalSeconds;
                        while (Time.realtimeSinceStartupAsDouble < retryAt)
                        {
                            runToken.ThrowIfCancellationRequested();
                            await Task.Yield();
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    var totalMilliseconds = (Time.realtimeSinceStartupAsDouble - startedAt) * 1000.0;
                    lastReport = BuildReport(
                        "FAIL_TIMEOUT",
                        attempts,
                        totalMilliseconds,
                        lastAttemptMilliseconds,
                        lastResult);
                    var timeout = new TimeoutException(lastReport);
                    lastError = timeout;
                    throw timeout;
                }
                finally
                {
                    runtimeDriver.AutoRun = previousAutoRun;
                    isRunning = false;
                }
            }
        }

        private string BuildReport(
            string status,
            int attempts,
            double totalMilliseconds,
            double lastAttemptMilliseconds,
            OcrPumpResult result)
        {
            var builder = new StringBuilder(1024);
            builder.AppendLine("PhraseLayer Quest OCR smoke test " + status);
            builder.Append("attempts=").Append(attempts)
                .Append(" total_ms=").Append(totalMilliseconds.ToString("F1"))
                .Append(" last_attempt_ms=").Append(lastAttemptMilliseconds.ToString("F1"))
                .AppendLine();

            if (result == null)
            {
                builder.AppendLine("camera_state=unobserved schedule_status=unobserved presented=false frame_timestamp_us=unobserved");
            }
            else
            {
                builder.Append("camera_state=").Append(result.CameraState)
                    .Append(" schedule_status=")
                    .Append(result.ScheduleStatus.HasValue ? result.ScheduleStatus.Value.ToString() : "unobserved")
                    .Append(" presented=").Append(result.Presented ? "true" : "false")
                    .Append(" frame_timestamp_us=")
                    .Append(result.FrameTimestampMicroseconds.HasValue
                        ? result.FrameTimestampMicroseconds.Value.ToString()
                        : "unobserved")
                    .AppendLine();
            }

            builder.Append("regions=").Append(presenter.Regions.Count)
                .Append(" overall_confidence=").Append(presenter.LastConfidence.ToString("F6"))
                .Append(" text_length=").Append(presenter.LastText.Length)
                .AppendLine();
            builder.AppendLine("dictionary_manifest=" + bootstrap.DictionaryManifestReport);
            builder.AppendLine("runtime_contract=" + bootstrap.RuntimeContractReport);

            if (includeRecognizedTextInReport)
                builder.AppendLine("recognized_text=" + presenter.LastText);
            else
                builder.AppendLine("recognized_text=<redacted; enable includeRecognizedTextInReport explicitly>");

            return builder.ToString().TrimEnd();
        }

        private void ValidateSettings()
        {
            if (timeoutSeconds <= 0f || float.IsNaN(timeoutSeconds) || float.IsInfinity(timeoutSeconds))
                throw new InvalidOperationException("Quest OCR smoke timeoutSeconds must be finite and greater than zero.");
            if (retryIntervalSeconds <= 0f || float.IsNaN(retryIntervalSeconds) || float.IsInfinity(retryIntervalSeconds))
                throw new InvalidOperationException("Quest OCR smoke retryIntervalSeconds must be finite and greater than zero.");
            if (minimumRecognizedRegions < 1)
                throw new InvalidOperationException("Quest OCR smoke minimumRecognizedRegions must be at least one.");
        }

        private void EnsureReferences()
        {
            if (runtimeDriver == null)
                throw new InvalidOperationException("Assign OcrDebugRuntimeBehaviour to QuestOcrSmokeTestBehaviour.");
            if (presenter == null)
                throw new InvalidOperationException("Assign OcrViewportDebugBehaviour to QuestOcrSmokeTestBehaviour.");
            if (bootstrap == null)
                throw new InvalidOperationException("Assign UnityPaddleOcrBootstrapBehaviour to QuestOcrSmokeTestBehaviour.");
        }
    }
}
