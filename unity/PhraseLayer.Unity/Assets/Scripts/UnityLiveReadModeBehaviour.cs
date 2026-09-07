using System;
using System.Threading;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Pipeline;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Connects recognized OCR to adaptive Read Mode. Configuration and accepted-frame generations protect
    /// presentation from obsolete success, failure and skip results. Platform awaits retain the Unity context.
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
        public long SupersededErrorCount { get; private set; }

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
                    lifetime?.Dispose(); lifetime = null;
                    coordinator?.Dispose(); coordinator = null;
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
                // Skip diagnostics are cumulative; an old/duplicate input must not rewrite the latest display status.
                if (result.Status == LiveReadModeProcessingStatus.Superseded) { SupersededObservationCount++; return; }
                if (result.Status == LiveReadModeProcessingStatus.StaleInput) { StaleObservationCount++; return; }
                if (result.Status != LiveReadModeProcessingStatus.Processed) throw new ArgumentOutOfRangeException();
                if (localCoordinator.LatestAcceptedTimestampMicroseconds != result.FrameTimestampMicroseconds)
                {
                    SupersededObservationCount++;
                    return;
                }
                if (result.Aligned == null) throw new InvalidOperationException("Processed live Read Mode result is missing aligned output.");
                localTracking.ProjectFitAndTrack(result.Aligned, result.FrameTimestampMicroseconds);
                LastAlignedResult = result.Aligned;
                LastProcessingStatus = result.Status;
                ProcessedObservationCount++;
                LastError = null;
            }
            catch (OperationCanceledException) when (localLifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (!IsCurrent(localCoordinator, localLifetime, localTracking, localGeneration)) { }
            catch (Exception exception)
            {
                if (!IsCurrent(localCoordinator, localLifetime, localTracking, localGeneration)) return;
                // Unlike an obsolete cancellation, an adapter failure is not converted to Superseded by Core.
                // It may arrive after a newer success, so check frame ownership before touching any presentation.
                if (frame != null && localCoordinator.LatestAcceptedTimestampMicroseconds != frame.TimestampMicroseconds)
                {
                    SupersededErrorCount++;
                    return;
                }
                LastError = exception;
                LastAlignedResult = null;
                LastProcessingStatus = null;
                localTracking.ResetTracking();
                Debug.LogException(exception, this);
            }
        }
    }
}
