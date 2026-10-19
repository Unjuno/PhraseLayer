using System;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;

namespace PhraseLayer.Core.Pipeline
{
    public enum LiveReadModeProcessingStatus { Processed = 0, Superseded = 1, StaleInput = 2 }

    public sealed class LiveReadModeProcessingResult
    {
        public LiveReadModeProcessingResult(LiveReadModeProcessingStatus status, long frameTimestampMicroseconds, ReadModeAlignedResult? aligned)
        {
            if (frameTimestampMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(frameTimestampMicroseconds));
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
    /// Latest-observation-wins coordinator. Superseding requests signal cancellation; only the operation owner
    /// releases its token source, after both processing and concurrent/reentrant Cancel calls have exited.
    /// Platform awaits retain the caller synchronization context. Callbacks never run under either lifecycle lock.
    /// </summary>
    public sealed class LiveReadModeCoordinator : IDisposable
    {
        private readonly ReadModeObservationProcessor processor;
        private readonly object gate = new object();
        private OperationCancellation? activeCancellation;
        private long latestAcceptedTimestampMicroseconds = -1;
        private long generation;
        private long cancellationCallbackFailureCount;
        private bool disposed;

        public LiveReadModeCoordinator(ReadModeObservationProcessor processor)
        {
            this.processor = processor ?? throw new ArgumentNullException(nameof(processor));
        }

        public long? LatestAcceptedTimestampMicroseconds
        {
            get { lock (gate) return latestAcceptedTimestampMicroseconds < 0 ? (long?)null : latestAcceptedTimestampMicroseconds; }
        }

        /// <summary>
        /// Cumulative callback exceptions, including external-token propagation. Bad callbacks must be fixed,
        /// but cannot strand a newer request. Only a count is retained, not potentially sensitive exception text.
        /// </summary>
        public long CancellationCallbackFailureCount => Interlocked.Read(ref cancellationCallbackFailureCount);

        public async Task<LiveReadModeProcessingResult> SubmitAsync(ImageFrame frame, OcrObservation observation,
            AssistancePolicy policy, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            cancellationToken.ThrowIfCancellationRequested();

            OperationCancellation localCancellation;
            OperationCancellation? previousCancellation;
            long localGeneration;
            lock (gate)
            {
                ThrowIfDisposed();
                if (frame.TimestampMicroseconds <= latestAcceptedTimestampMicroseconds)
                    return Skipped(LiveReadModeProcessingStatus.StaleInput, frame.TimestampMicroseconds);
                localCancellation = new OperationCancellation(cancellationToken, RecordCancellationFailures);
                latestAcceptedTimestampMicroseconds = frame.TimestampMicroseconds;
                localGeneration = ++generation;
                previousCancellation = activeCancellation;
                activeCancellation = localCancellation;
            }

            // Captured before any callback can reenter the coordinator and cancel the newly published request.
            var localToken = localCancellation.Token;
            try
            {
                previousCancellation?.Cancel();
                ReadModeAlignedResult aligned;
                try
                {
                    localToken.ThrowIfCancellationRequested();
                    aligned = await processor.ProcessAlignedAsync(frame, observation, policy, localToken);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (localToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    return Skipped(LiveReadModeProcessingStatus.Superseded, frame.TimestampMicroseconds);
                }

                lock (gate)
                {
                    if (disposed || localGeneration != generation || frame.TimestampMicroseconds != latestAcceptedTimestampMicroseconds)
                        return Skipped(LiveReadModeProcessingStatus.Superseded, frame.TimestampMicroseconds);
                }
                return new LiveReadModeProcessingResult(LiveReadModeProcessingStatus.Processed, frame.TimestampMicroseconds, aligned);
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(activeCancellation, localCancellation)) activeCancellation = null;
                }
                localCancellation.Complete();
            }
        }

        public void CancelActive()
        {
            OperationCancellation? cancellation;
            lock (gate)
            {
                ThrowIfDisposed();
                generation++;
                cancellation = activeCancellation;
                activeCancellation = null;
            }
            cancellation?.Cancel();
        }

        public void Reset()
        {
            OperationCancellation? cancellation;
            lock (gate)
            {
                ThrowIfDisposed();
                generation++;
                cancellation = activeCancellation;
                activeCancellation = null;
                latestAcceptedTimestampMicroseconds = -1;
            }
            cancellation?.Cancel();
        }

        public void Dispose()
        {
            OperationCancellation? cancellation;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                generation++;
                cancellation = activeCancellation;
                activeCancellation = null;
            }
            cancellation?.Cancel();
        }

        private static LiveReadModeProcessingResult Skipped(LiveReadModeProcessingStatus status, long timestamp) =>
            new LiveReadModeProcessingResult(status, timestamp, null);

        private void RecordCancellationFailures(int count) => Interlocked.Add(ref cancellationCallbackFailureCount, count);
        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(LiveReadModeCoordinator));
        }

        private sealed class OperationCancellation
        {
            private readonly object lifetimeGate = new object();
            private readonly CancellationTokenSource source = new CancellationTokenSource();
            private readonly Action<int> reportFailures;
            private readonly CancellationTokenRegistration externalRegistration;
            private int cancellationCalls;
            private bool operationCompleted;
            private bool resourcesReleased;

            public OperationCancellation(CancellationToken externalToken, Action<int> reportFailures)
            {
                this.reportFailures = reportFailures;
                Token = source.Token;
                // An explicit registration makes external cancellation obey the same Cancel/Dispose exclusion.
                // Register may call synchronously for an already-cancelled token; no operation is published yet.
                try { externalRegistration = externalToken.Register(state => ((OperationCancellation)state!).Cancel(), this); }
                catch { source.Dispose(); throw; }
            }
            public CancellationToken Token { get; }

            public void Cancel()
            {
                lock (lifetimeGate)
                {
                    if (operationCompleted || resourcesReleased) return;
                    cancellationCalls++;
                }
                try
                {
                    try { source.Cancel(); }
                    catch (AggregateException exception) { reportFailures(exception.Flatten().InnerExceptions.Count); }
                }
                finally
                {
                    bool release;
                    lock (lifetimeGate)
                    {
                        cancellationCalls--;
                        release = TryClaimRelease();
                    }
                    if (release) ReleaseResources();
                }
            }

            public void Complete()
            {
                bool release;
                lock (lifetimeGate)
                {
                    operationCompleted = true;
                    release = TryClaimRelease();
                }
                if (release) ReleaseResources();
            }

            // Call only while holding lifetimeGate. Claim before disposing so no later Cancel can enter.
            private bool TryClaimRelease()
            {
                if (!operationCompleted || cancellationCalls != 0 || resourcesReleased) return false;
                resourcesReleased = true;
                return true;
            }
            private void ReleaseResources()
            {
                // Registration disposal may wait for a callback, so neither coordinator nor lifecycle lock is held.
                try { externalRegistration.Dispose(); }
                finally { source.Dispose(); }
            }
        }
    }
}
