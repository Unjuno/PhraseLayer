using System;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;

namespace PhraseLayer.Core.Pipeline
{
    public enum LiveReadModeProcessingStatus
    {
        Processed = 0,
        Superseded = 1,
        StaleInput = 2
    }

    public sealed class LiveReadModeProcessingResult
    {
        public LiveReadModeProcessingResult(
            LiveReadModeProcessingStatus status,
            long frameTimestampMicroseconds,
            ReadModeAlignedResult? aligned)
        {
            if (frameTimestampMicroseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(frameTimestampMicroseconds));
            if (status == LiveReadModeProcessingStatus.Processed && aligned == null)
                throw new ArgumentException("Processed live Read Mode results require aligned output.", nameof(aligned));
            if (status != LiveReadModeProcessingStatus.Processed && aligned != null)
                throw new ArgumentException("Skipped live Read Mode results must not carry aligned output.", nameof(aligned));

            Status = status;
            FrameTimestampMicroseconds = frameTimestampMicroseconds;
            Aligned = aligned;
        }

        public LiveReadModeProcessingStatus Status { get; }
        public long FrameTimestampMicroseconds { get; }
        public ReadModeAlignedResult? Aligned { get; }
        public bool WasProcessed => Status == LiveReadModeProcessingStatus.Processed;
    }

    /// <summary>
    /// Latest-observation-wins coordinator for live OCR → adaptive Read Mode processing.
    ///
    /// A newer frame cancels an older in-flight language/alignment operation. If an adapter ignores cancellation and
    /// the older operation eventually completes, its generation is still rejected as Superseded, preventing stale
    /// semantic/world-space output from replacing the result for a newer camera observation.
    /// Cancellation callbacks are never invoked while the coordinator lock is held. Each SubmitAsync invocation owns
    /// disposal of its linked CancellationTokenSource after the platform adapter has finished unwinding cancellation.
    /// </summary>
    public sealed class LiveReadModeCoordinator : IDisposable
    {
        private readonly ReadModeObservationProcessor processor;
        private readonly object gate = new object();
        private CancellationTokenSource? activeCancellation;
        private long latestAcceptedTimestampMicroseconds = -1;
        private long generation;
        private bool disposed;

        public LiveReadModeCoordinator(ReadModeObservationProcessor processor)
        {
            this.processor = processor ?? throw new ArgumentNullException(nameof(processor));
        }

        public long? LatestAcceptedTimestampMicroseconds
        {
            get
            {
                lock (gate)
                {
                    return latestAcceptedTimestampMicroseconds < 0
                        ? (long?)null
                        : latestAcceptedTimestampMicroseconds;
                }
            }
        }

        public async Task<LiveReadModeProcessingResult> SubmitAsync(
            ImageFrame frame,
            OcrObservation observation,
            AssistancePolicy policy,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            cancellationToken.ThrowIfCancellationRequested();

            CancellationTokenSource localCancellation;
            CancellationTokenSource? previousCancellation;
            long localGeneration;
            lock (gate)
            {
                ThrowIfDisposed();
                if (frame.TimestampMicroseconds <= latestAcceptedTimestampMicroseconds)
                {
                    return new LiveReadModeProcessingResult(
                        LiveReadModeProcessingStatus.StaleInput,
                        frame.TimestampMicroseconds,
                        null);
                }

                latestAcceptedTimestampMicroseconds = frame.TimestampMicroseconds;
                localGeneration = ++generation;
                previousCancellation = activeCancellation;
                localCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                activeCancellation = localCancellation;
            }

            Cancel(previousCancellation);
            var localToken = localCancellation.Token;
            ReadModeAlignedResult aligned;
            try
            {
                aligned = await processor.ProcessAlignedAsync(
                    frame,
                    observation,
                    policy,
                    localToken);
            }
            catch (OperationCanceledException) when (
                localToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new LiveReadModeProcessingResult(
                    LiveReadModeProcessingStatus.Superseded,
                    frame.TimestampMicroseconds,
                    null);
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(activeCancellation, localCancellation))
                        activeCancellation = null;
                }
                localCancellation.Dispose();
            }

            lock (gate)
            {
                if (disposed ||
                    localGeneration != generation ||
                    frame.TimestampMicroseconds != latestAcceptedTimestampMicroseconds)
                {
                    return new LiveReadModeProcessingResult(
                        LiveReadModeProcessingStatus.Superseded,
                        frame.TimestampMicroseconds,
                        null);
                }
            }

            return new LiveReadModeProcessingResult(
                LiveReadModeProcessingStatus.Processed,
                frame.TimestampMicroseconds,
                aligned);
        }

        public void CancelActive()
        {
            CancellationTokenSource? cancellation;
            lock (gate)
            {
                ThrowIfDisposed();
                generation++;
                cancellation = activeCancellation;
                activeCancellation = null;
            }
            Cancel(cancellation);
        }

        public void Reset()
        {
            CancellationTokenSource? cancellation;
            lock (gate)
            {
                ThrowIfDisposed();
                generation++;
                cancellation = activeCancellation;
                activeCancellation = null;
                latestAcceptedTimestampMicroseconds = -1;
            }
            Cancel(cancellation);
        }

        public void Dispose()
        {
            CancellationTokenSource? cancellation;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                generation++;
                cancellation = activeCancellation;
                activeCancellation = null;
            }
            Cancel(cancellation);
        }

        private static void Cancel(CancellationTokenSource? cancellation)
        {
            cancellation?.Cancel();
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(LiveReadModeCoordinator));
        }
    }
}
