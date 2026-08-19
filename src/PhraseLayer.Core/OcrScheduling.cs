using System;
using System.Threading;
using System.Threading.Tasks;

namespace PhraseLayer.Core.Inputs
{
    public enum OcrScheduleStatus
    {
        Processed = 0,
        SkippedBusy = 1,
        SkippedRateLimit = 2,
        SkippedStale = 3
    }

    public sealed class OcrScheduleResult
    {
        public OcrScheduleResult(OcrScheduleStatus status, long frameTimestampMicroseconds, OcrObservation? observation)
        {
            Status = status;
            FrameTimestampMicroseconds = frameTimestampMicroseconds;
            Observation = observation;
        }

        public OcrScheduleStatus Status { get; }
        public long FrameTimestampMicroseconds { get; }
        public OcrObservation? Observation { get; }
        public bool WasProcessed => Status == OcrScheduleStatus.Processed;
    }

    /// <summary>
    /// Prevents camera-frequency OCR from becoming inference-frequency OCR.
    /// At most one inference is active, stale frames are rejected, and processed frames are rate-limited by source timestamp.
    /// </summary>
    public sealed class OcrFrameScheduler : IDisposable
    {
        private readonly IOcrEngine engine;
        private readonly SemaphoreSlim singleFlight = new SemaphoreSlim(1, 1);
        private readonly long minimumIntervalMicroseconds;
        private long lastProcessedTimestampMicroseconds = long.MinValue;
        private bool disposed;

        public OcrFrameScheduler(IOcrEngine engine, double maxInferencesPerSecond)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            if (double.IsNaN(maxInferencesPerSecond) || double.IsInfinity(maxInferencesPerSecond) || maxInferencesPerSecond <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(maxInferencesPerSecond));

            minimumIntervalMicroseconds = Math.Max(1L, (long)Math.Ceiling(1_000_000.0 / maxInferencesPerSecond));
        }

        public long MinimumIntervalMicroseconds => minimumIntervalMicroseconds;
        public long? LastProcessedTimestampMicroseconds =>
            lastProcessedTimestampMicroseconds == long.MinValue ? (long?)null : lastProcessedTimestampMicroseconds;

        public async Task<OcrScheduleResult> TryProcessAsync(
            ImageFrame frame,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            if (!singleFlight.Wait(0))
                return new OcrScheduleResult(OcrScheduleStatus.SkippedBusy, frame.TimestampMicroseconds, null);

            try
            {
                if (lastProcessedTimestampMicroseconds != long.MinValue)
                {
                    if (frame.TimestampMicroseconds <= lastProcessedTimestampMicroseconds)
                        return new OcrScheduleResult(OcrScheduleStatus.SkippedStale, frame.TimestampMicroseconds, null);

                    var elapsed = frame.TimestampMicroseconds - lastProcessedTimestampMicroseconds;
                    if (elapsed < minimumIntervalMicroseconds)
                        return new OcrScheduleResult(OcrScheduleStatus.SkippedRateLimit, frame.TimestampMicroseconds, null);
                }

                var observation = await engine.RecognizeAsync(frame, cancellationToken).ConfigureAwait(false);
                lastProcessedTimestampMicroseconds = frame.TimestampMicroseconds;
                return new OcrScheduleResult(OcrScheduleStatus.Processed, frame.TimestampMicroseconds, observation);
            }
            finally
            {
                singleFlight.Release();
            }
        }

        public void Reset()
        {
            ThrowIfDisposed();
            if (singleFlight.CurrentCount == 0)
                throw new InvalidOperationException("Cannot reset OCR scheduling while inference is active.");
            lastProcessedTimestampMicroseconds = long.MinValue;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            singleFlight.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(OcrFrameScheduler));
        }
    }
}
