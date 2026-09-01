using System;
using System.Threading;
using System.Threading.Tasks;

namespace PhraseLayer.Core.Inputs
{
    public enum OcrPumpStatus
    {
        Presented = 0,
        SkippedPumpBusy = 1,
        CameraUnavailable = 2,
        SkippedOcrBusy = 3,
        SkippedRateLimit = 4,
        SkippedStale = 5
    }

    public sealed class OcrPumpResult
    {
        public OcrPumpResult(
            OcrPumpStatus status,
            CameraCaptureState cameraState,
            long? frameTimestampMicroseconds,
            OcrScheduleStatus? scheduleStatus,
            bool presented)
        {
            Status = status;
            CameraState = cameraState;
            FrameTimestampMicroseconds = frameTimestampMicroseconds;
            ScheduleStatus = scheduleStatus;
            Presented = presented;
        }

        public OcrPumpStatus Status { get; }
        public CameraCaptureState CameraState { get; }
        public long? FrameTimestampMicroseconds { get; }
        public OcrScheduleStatus? ScheduleStatus { get; }
        public bool Presented { get; }
    }

    /// <summary>
    /// Executes one end-to-end camera → OCR scheduler → presentation cycle.
    /// The pump itself is single-flight so an overlapping caller cannot start another camera capture
    /// while the current frame is still being captured or inferred.
    /// Await points preserve the caller synchronization context so Unity-bound adapters remain on their owner thread.
    /// A native payload may retain immutable capture metadata after its image resource is released so downstream
    /// Read Mode geometry can still use the exact capture pose/timestamp without keeping a GPU frame alive.
    /// </summary>
    public sealed class OcrRuntimePump
    {
        private readonly CameraCaptureCoordinator camera;
        private readonly OcrFrameScheduler scheduler;
        private readonly OcrPresentationCoordinator presenter;
        private readonly SemaphoreSlim singleFlight = new SemaphoreSlim(1, 1);

        public OcrRuntimePump(
            CameraCaptureCoordinator camera,
            OcrFrameScheduler scheduler,
            OcrPresentationCoordinator presenter)
        {
            this.camera = camera ?? throw new ArgumentNullException(nameof(camera));
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            this.presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        }

        public async Task<OcrPumpResult> TryRunOnceAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!singleFlight.Wait(0))
            {
                return new OcrPumpResult(
                    OcrPumpStatus.SkippedPumpBusy,
                    camera.State,
                    null,
                    null,
                    false);
            }

            ImageFrame? frame = null;
            try
            {
                frame = await camera.CaptureAsync(cancellationToken);
                if (frame == null)
                {
                    return new OcrPumpResult(
                        OcrPumpStatus.CameraUnavailable,
                        camera.State,
                        null,
                        null,
                        false);
                }

                var scheduleResult = await scheduler.TryProcessAsync(frame, cancellationToken);
                if (scheduleResult.WasProcessed)
                {
                    var presented = presenter.PresentIfProcessed(scheduleResult, frame);
                    return new OcrPumpResult(
                        OcrPumpStatus.Presented,
                        camera.State,
                        frame.TimestampMicroseconds,
                        scheduleResult.Status,
                        presented);
                }

                return new OcrPumpResult(
                    MapSkippedStatus(scheduleResult.Status),
                    camera.State,
                    frame.TimestampMicroseconds,
                    scheduleResult.Status,
                    false);
            }
            finally
            {
                ReleaseNativeImageResource(frame);
                singleFlight.Release();
            }
        }

        private static void ReleaseNativeImageResource(ImageFrame? frame)
        {
            if (frame?.NativePayload is IReleasableImageFramePayload releasable)
                releasable.ReleaseImageResource();
        }

        private static OcrPumpStatus MapSkippedStatus(OcrScheduleStatus status)
        {
            switch (status)
            {
                case OcrScheduleStatus.SkippedBusy:
                    return OcrPumpStatus.SkippedOcrBusy;
                case OcrScheduleStatus.SkippedRateLimit:
                    return OcrPumpStatus.SkippedRateLimit;
                case OcrScheduleStatus.SkippedStale:
                    return OcrPumpStatus.SkippedStale;
                case OcrScheduleStatus.Processed:
                    throw new InvalidOperationException("Processed OCR status must be handled before skipped-status mapping.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown OCR scheduler status.");
            }
        }
    }
}
