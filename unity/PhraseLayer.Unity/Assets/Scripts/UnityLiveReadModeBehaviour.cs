using System;
using System.Threading;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Pipeline;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Connects the already-recognized live OCR stream to adaptive Read Mode without invoking OCR again.
    /// A newer observation supersedes older in-flight language work through LiveReadModeCoordinator, then only the
    /// accepted latest result is projected, fitted, tracked, and optionally rendered in world space.
    /// </summary>
    public sealed class UnityLiveReadModeBehaviour : MonoBehaviour
    {
        [SerializeField] private OcrViewportDebugBehaviour ocrPresenter = default(OcrViewportDebugBehaviour);
        [SerializeField] private UnityWorldTextTrackingBehaviour worldTextTracking = default(UnityWorldTextTrackingBehaviour);
        [SerializeField] private AssistanceMode assistanceMode = AssistanceMode.Balanced;

        private LiveReadModeCoordinator coordinator;
        private CancellationTokenSource lifetime;
        private bool subscribed;

        public bool IsConfigured => coordinator != null && ocrPresenter != null && worldTextTracking != null;
        public AssistanceMode AssistanceMode => assistanceMode;
        public LiveReadModeProcessingStatus? LastProcessingStatus { get; private set; }
        public ReadModeAlignedResult LastAlignedResult { get; private set; }
        public Exception LastError { get; private set; }
        public long ProcessedObservationCount { get; private set; }
        public long SupersededObservationCount { get; private set; }
        public long StaleObservationCount { get; private set; }
        public long UnconfiguredObservationCount { get; private set; }

        public void SetSceneReferences(
            OcrViewportDebugBehaviour presenter,
            UnityWorldTextTrackingBehaviour tracking)
        {
            if (presenter == null) throw new ArgumentNullException(nameof(presenter));
            if (tracking == null) throw new ArgumentNullException(nameof(tracking));

            Unsubscribe();
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
            coordinator?.Dispose();
            coordinator = new LiveReadModeCoordinator(processor);
            LastProcessingStatus = null;
            LastAlignedResult = null;
            LastError = null;
        }

        public void SetAssistanceMode(AssistanceMode mode)
        {
            assistanceMode = mode;
        }

        private void OnEnable()
        {
            lifetime?.Dispose();
            lifetime = new CancellationTokenSource();
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
            lifetime?.Cancel();
            if (coordinator != null)
            {
                try
                {
                    coordinator.CancelActive();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void OnDestroy()
        {
            Unsubscribe();
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;
            coordinator?.Dispose();
            coordinator = null;
        }

        private void SubscribeIfEnabledLifetimeExists()
        {
            if (lifetime != null && !lifetime.IsCancellationRequested)
                Subscribe();
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

        private async void OnObservationPresented(OcrObservation observation, ImageFrame frame)
        {
            var localCoordinator = coordinator;
            var localLifetime = lifetime;
            if (localCoordinator == null || worldTextTracking == null || localLifetime == null || localLifetime.IsCancellationRequested)
            {
                UnconfiguredObservationCount++;
                return;
            }

            try
            {
                var result = await localCoordinator.SubmitAsync(
                    frame,
                    observation,
                    AssistancePolicy.ForMode(assistanceMode),
                    localLifetime.Token);
                LastProcessingStatus = result.Status;

                switch (result.Status)
                {
                    case LiveReadModeProcessingStatus.Processed:
                        if (result.Aligned == null)
                            throw new InvalidOperationException("Processed live Read Mode result is missing aligned output.");
                        LastAlignedResult = result.Aligned;
                        worldTextTracking.ProjectFitAndTrack(
                            result.Aligned,
                            result.FrameTimestampMicroseconds);
                        ProcessedObservationCount++;
                        LastError = null;
                        break;
                    case LiveReadModeProcessingStatus.Superseded:
                        SupersededObservationCount++;
                        break;
                    case LiveReadModeProcessingStatus.StaleInput:
                        StaleObservationCount++;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            catch (OperationCanceledException) when (localLifetime.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (localLifetime.IsCancellationRequested || !ReferenceEquals(localCoordinator, coordinator))
            {
            }
            catch (Exception exception)
            {
                LastError = exception;
                Debug.LogException(exception, this);
            }
        }
    }
}
