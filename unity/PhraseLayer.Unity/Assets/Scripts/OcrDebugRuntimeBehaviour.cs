using System;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Inputs;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Thin Unity driver for the platform-neutral OCR runtime pump.
    /// A concrete IOcrEngine is injected by bootstrap code; the legacy Unity texture backend path remains as a compatibility adapter.
    /// </summary>
    public sealed class OcrDebugRuntimeBehaviour : MonoBehaviour
    {
        [SerializeField] private MetaPassthroughCameraBridge cameraBridge = default(MetaPassthroughCameraBridge);
        [SerializeField] private OcrViewportDebugBehaviour presenter = default(OcrViewportDebugBehaviour);
        [SerializeField] private bool autoRun = true;
        [SerializeField] private float targetOcrHz = 5f;

        private CancellationTokenSource lifetime;
        private OcrRuntimePump pump;
        private Task<OcrPumpResult> activeRun;
        private double nextAttemptTime;
        private Exception lastError;
        private OcrPumpResult lastResult;

        public bool IsConfigured => pump != null;
        public bool IsRunning => activeRun != null && !activeRun.IsCompleted;
        public Exception LastError => lastError;
        public OcrPumpResult LastResult => lastResult;
        public float TargetOcrHz => targetOcrHz;

        public void ConfigureBackend(IUnityTextureOcrBackend backend)
        {
            if (backend == null) throw new ArgumentNullException(nameof(backend));
            ConfigureEngine(new UnityTextureOcrEngine(backend));
        }

        public void ConfigureEngine(IOcrEngine engine)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));
            EnsureReferences();
            if (targetOcrHz <= 0f) throw new InvalidOperationException("Target OCR Hz must be greater than zero.");

            var camera = new CameraCaptureCoordinator(
                new MetaPassthroughCameraPermissionService(),
                cameraBridge);
            var scheduler = new OcrFrameScheduler(engine, targetOcrHz);
            var presentation = new OcrPresentationCoordinator(presenter);

            pump = new OcrRuntimePump(camera, scheduler, presentation);
            lastError = null;
            lastResult = null;
            nextAttemptTime = 0.0;
        }

        public async Task<OcrPumpResult> RunOnceAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (pump == null)
                throw new InvalidOperationException("Configure an OCR engine before running the OCR runtime driver.");

            var result = await pump.TryRunOnceAsync(cancellationToken);
            lastResult = result;

            if (!result.Presented && result.ScheduleStatus.HasValue && result.FrameTimestampMicroseconds.HasValue)
            {
                presenter.SetScheduleStatus(
                    result.ScheduleStatus.Value,
                    result.FrameTimestampMicroseconds.Value);
            }

            return result;
        }

        private void OnEnable()
        {
            lifetime?.Dispose();
            lifetime = new CancellationTokenSource();
            nextAttemptTime = 0.0;
        }

        private void Update()
        {
            ObserveCompletedRun();
            if (!autoRun || pump == null || IsRunning || lifetime == null || lifetime.IsCancellationRequested)
                return;

            var now = Time.realtimeSinceStartupAsDouble;
            if (now < nextAttemptTime) return;

            nextAttemptTime = now + 1.0 / Math.Max(0.001, targetOcrHz);
            activeRun = RunOnceAsync(lifetime.Token);
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

        private void ObserveCompletedRun()
        {
            if (activeRun == null || !activeRun.IsCompleted) return;

            if (activeRun.IsFaulted)
            {
                lastError = activeRun.Exception?.GetBaseException();
                if (lastError != null) Debug.LogException(lastError);
            }
            else if (activeRun.IsCanceled)
            {
                lastError = null;
            }
            else
            {
                lastError = null;
                lastResult = activeRun.Result;
            }

            activeRun = null;
        }

        private void EnsureReferences()
        {
            if (cameraBridge == null)
                throw new InvalidOperationException("Assign MetaPassthroughCameraBridge before configuring the OCR runtime driver.");
            if (presenter == null)
                throw new InvalidOperationException("Assign OcrViewportDebugBehaviour before configuring the OCR runtime driver.");
        }
    }
}
