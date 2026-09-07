using System;
using System.Threading;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Pipeline;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Connects recognized OCR to adaptive Read Mode. In addition to camera-generation checks in Core,
    /// a scene-configuration generation rejects results crossing a presenter, mode, processor or lifetime change.
    /// All component access stays on the caller's Unity synchronization context.
    /// </summary>
    public sealed class UnityLiveReadModeBehaviour : MonoBehaviour
    {
        [SerializeField] private OcrViewportDebugBehaviour ocrPresenter = default(OcrViewportDebugBehaviour);
        [SerializeField] private UnityWorldTextTrackingBehaviour worldTextTracking = default(UnityWorldTextTrackingBehaviour);
        [SerializeField] private AssistanceMode assistanceMode = AssistanceMode.Balanced;

        private LiveReadModeCoordinator coordinator;
        private CancellationTokenSource lifetime;
        private bool subscribed;
        private long configurationGeneration;

        public bool IsConfigured => coordinator != null && ocrPresenter != null && worldTextTracking != null;
        public AssistanceMode AssistanceMode => assistanceMode;
        public LiveReadModeProcessingStatus? LastProcessingStatus { get; private set; }
        public ReadModeAlignedResult LastAlignedResult { get; private set; }
        public Exception LastError { get; private set; }
        public long ProcessedObservationCount { get; private set; }
        public long SupersededObservationCount { get; private set; }
        public long StaleObservationCount { get; private set; }
        public long UnconfiguredObservationCount { get; private set; }

        public void SetSceneReferences(OcrViewportDebugBehaviour presenter, UnityWorldTextTrackingBehaviour tracking)
        {
            if (presenter == null) throw new ArgumentNullException(nameof(presenter));
            if (tracking == null) throw new ArgumentNullException(nameof(tracking));
            Unsubscribe();
            InvalidatePresentation();
            ocrPresenter = presenter;
            worldTextTracking = tracking;
            SubscribeIfEnabledLifetimeExists();
        }

        public void ConfigureLanguagePipeline(LanguagePipeline languagePipeline)
        {
            if (languagePipeline == null) throw new ArgumentNullException(nameof(languagePipeline));
            ConfigureProcessor(new ReadModeObservationProcessor(languagePipeline));
        }

        public void ConfigureProcessor(ReadModeObservationProcessor processor)
        {
            if (processor == null) throw new ArgumentNullException(nameof(processor));
            InvalidatePresentation();
            coordinator?.Dispose();
            coordinator = new LiveReadModeCoordinator(processor);
            LastError = null;
        }

        public void SetAssistanceMode(AssistanceMode mode)
        {
            if (!Enum.IsDefined(typeof(AssistanceMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            if (assistanceMode == mode) return;
            InvalidatePresentation();
            assistanceMode = mode;
        }

        private void OnEnable()
        {
            InvalidatePresentation();
            lifetime?.Dispose();
            lifetime = new CancellationTokenSource();
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
            try { InvalidatePresentation(); }
            finally { lifetime?.Cancel(); }
        }

        private void OnDestroy()
        {
            Unsubscribe();
            try { InvalidatePresentation(); }
            finally
            {
                try { lifetime?.Cancel(); }
                finally
                {
                    lifetime?.Dispose();
                    lifetime = null;
                    coordinator?.Dispose();
                    coordinator = null;
                }
            }
        }

        private void InvalidatePresentation()
        {
            configurationGeneration++;
            LastProcessingStatus = null;
            LastAlignedResult = null;
            try { coordinator?.CancelActive(); }
            catch (ObjectDisposedException) { }
            finally { worldTextTracking?.ResetTracking(); }
        }

        private void SubscribeIfEnabledLifetimeExists()
        {
            if (lifetime != null && !lifetime.IsCancellationRequested) Subscribe();
        }
        private void Subscribe()
        {
            if (subscribed || ocrPresenter == null) return;
            ocrPresenter.ObservationPresented += OnObservationPresented;
            subscribed = true;
        }
        private void Unsubscribe()
        {
            if (!subscribed || ocrPresenter == null) return;
            ocrPresenter.ObservationPresented -= OnObservationPresented;
            subscribed = false;
        }

        private bool IsCurrent(LiveReadModeCoordinator localCoordinator, CancellationTokenSource localLifetime,
            UnityWorldTextTrackingBehaviour localTracking, long localGeneration)
        {
            return localGeneration == configurationGeneration && ReferenceEquals(localCoordinator, coordinator) &&
                ReferenceEquals(localLifetime, lifetime) && ReferenceEquals(localTracking, worldTextTracking) &&
                localLifetime != null && !localLifetime.IsCancellationRequested;
        }

        private async void OnObservationPresented(OcrObservation observation, ImageFrame frame)
        {
            var localCoordinator = coordinator;
            var localLifetime = lifetime;
            var localTracking = worldTextTracking;
            var localGeneration = configurationGeneration;
            if (localCoordinator == null || localTracking == null || localLifetime == null || localLifetime.IsCancellationRequested)
            {
                UnconfiguredObservationCount++;
                return;
            }
            try
            {
                var result = await localCoordinator.SubmitAsync(frame, observation,
                    AssistancePolicy.ForMode(assistanceMode), localLifetime.Token);
                if (!IsCurrent(localCoordinator, localLifetime, localTracking, localGeneration)) return;
                LastProcessingStatus = result.Status;
                switch (result.Status)
                {
                    case LiveReadModeProcessingStatus.Processed:
                        if (result.Aligned == null)
                            throw new InvalidOperationException("Processed live Read Mode result is missing aligned output.");
                        localTracking.ProjectFitAndTrack(result.Aligned, result.FrameTimestampMicroseconds);
                        LastAlignedResult = result.Aligned;
                        ProcessedObservationCount++;
                        LastError = null;
                        break;
                    case LiveReadModeProcessingStatus.Superseded:
                        SupersededObservationCount++;
                        break;
                    case LiveReadModeProcessingStatus.StaleInput:
                        StaleObservationCount++;
                        break;
                    default: throw new ArgumentOutOfRangeException();
                }
            }
            catch (OperationCanceledException) when (localLifetime.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (!IsCurrent(localCoordinator, localLifetime, localTracking, localGeneration))
            {
            }
            catch (Exception exception)
            {
                if (!IsCurrent(localCoordinator, localLifetime, localTracking, localGeneration)) return;
                LastError = exception;
                LastAlignedResult = null;
                LastProcessingStatus = null;
                localTracking.ResetTracking();
                Debug.LogException(exception, this);
            }
        }
    }
}
