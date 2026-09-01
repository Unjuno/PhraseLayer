using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Assistance;
using PhraseLayer.Core.Inputs;

namespace PhraseLayer.Core.Pipeline
{
    public sealed class ListenModeProcessingTimings
    {
        public ListenModeProcessingTimings(
            double asrMilliseconds,
            double languagePlanMilliseconds,
            double totalMilliseconds)
        {
            if (asrMilliseconds < 0.0 || double.IsNaN(asrMilliseconds) || double.IsInfinity(asrMilliseconds))
                throw new ArgumentOutOfRangeException(nameof(asrMilliseconds));
            if (languagePlanMilliseconds < 0.0 || double.IsNaN(languagePlanMilliseconds) || double.IsInfinity(languagePlanMilliseconds))
                throw new ArgumentOutOfRangeException(nameof(languagePlanMilliseconds));
            if (totalMilliseconds < 0.0 || double.IsNaN(totalMilliseconds) || double.IsInfinity(totalMilliseconds))
                throw new ArgumentOutOfRangeException(nameof(totalMilliseconds));
            if (totalMilliseconds + 0.001 < asrMilliseconds || totalMilliseconds + 0.001 < languagePlanMilliseconds)
                throw new ArgumentException("Listen Mode total timing cannot be smaller than a measured phase.");

            AsrMilliseconds = asrMilliseconds;
            LanguagePlanMilliseconds = languagePlanMilliseconds;
            TotalMilliseconds = totalMilliseconds;
        }

        public double AsrMilliseconds { get; }
        public double LanguagePlanMilliseconds { get; }
        public double TotalMilliseconds { get; }
    }

    public sealed class ListenModeObservationResult
    {
        public ListenModeObservationResult(
            AudioChunk audio,
            AsrObservation observation,
            MixedLanguagePlan? languagePlan,
            ListenModeProcessingTimings timings)
        {
            Audio = audio ?? throw new ArgumentNullException(nameof(audio));
            Observation = observation ?? throw new ArgumentNullException(nameof(observation));
            if (languagePlan != null && !string.Equals(languagePlan.SourceText, observation.Text, StringComparison.Ordinal))
                throw new ArgumentException("Listen Mode language plan must match the ASR transcript.", nameof(languagePlan));

            LanguagePlan = languagePlan;
            Timings = timings ?? throw new ArgumentNullException(nameof(timings));
        }

        public AudioChunk Audio { get; }
        public AsrObservation Observation { get; }
        public MixedLanguagePlan? LanguagePlan { get; }
        public ListenModeProcessingTimings Timings { get; }
        public bool HasLanguagePlan => LanguagePlan != null;
    }

    /// <summary>
    /// ASR → adaptive-language handoff for one utterance/window. Partial ASR observations are exposed to
    /// the caller but are not translated by default, avoiding expensive translation churn while a transcript
    /// is still changing. Set planPartialObservations only for a UI that explicitly wants partial mixed text.
    /// Timings separate ASR from adaptive language planning so device measurements can identify the real bottleneck.
    /// </summary>
    public sealed class ListenModeObservationProcessor
    {
        private readonly IAsrEngine asr;
        private readonly LanguagePipeline language;
        private readonly bool planPartialObservations;

        public ListenModeObservationProcessor(
            IAsrEngine asr,
            LanguagePipeline language,
            bool planPartialObservations = false)
        {
            this.asr = asr ?? throw new ArgumentNullException(nameof(asr));
            this.language = language ?? throw new ArgumentNullException(nameof(language));
            this.planPartialObservations = planPartialObservations;
        }

        public async Task<ListenModeObservationResult> ProcessAsync(
            AudioChunk audio,
            AssistancePolicy policy,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (audio == null) throw new ArgumentNullException(nameof(audio));
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            cancellationToken.ThrowIfCancellationRequested();

            var totalStopwatch = Stopwatch.StartNew();
            var asrStopwatch = Stopwatch.StartNew();
            var observation = await asr.TranscribeAsync(audio, cancellationToken);
            asrStopwatch.Stop();

            var languagePlanMilliseconds = 0.0;
            MixedLanguagePlan? plan = null;
            if (!string.IsNullOrWhiteSpace(observation.Text) &&
                (observation.IsFinal || planPartialObservations))
            {
                var languageStopwatch = Stopwatch.StartNew();
                plan = await language.PlanAsync(
                    observation.Text,
                    policy,
                    observation.Text,
                    cancellationToken);
                languageStopwatch.Stop();
                languagePlanMilliseconds = languageStopwatch.Elapsed.TotalMilliseconds;
            }

            totalStopwatch.Stop();
            return new ListenModeObservationResult(
                audio,
                observation,
                plan,
                new ListenModeProcessingTimings(
                    asrStopwatch.Elapsed.TotalMilliseconds,
                    languagePlanMilliseconds,
                    totalStopwatch.Elapsed.TotalMilliseconds));
        }
    }

    public enum LiveListenModeProcessingStatus
    {
        Processed = 0,
        Superseded = 1,
        StaleInput = 2
    }

    public sealed class LiveListenModeProcessingResult
    {
        public LiveListenModeProcessingResult(
            LiveListenModeProcessingStatus status,
            long audioTimestampMicroseconds,
            ListenModeObservationResult? output)
        {
            if (audioTimestampMicroseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(audioTimestampMicroseconds));
            if (status == LiveListenModeProcessingStatus.Processed && output == null)
                throw new ArgumentException("Processed Listen Mode results require output.", nameof(output));
            if (status != LiveListenModeProcessingStatus.Processed && output != null)
                throw new ArgumentException("Skipped Listen Mode results must not carry output.", nameof(output));

            Status = status;
            AudioTimestampMicroseconds = audioTimestampMicroseconds;
            Output = output;
        }

        public LiveListenModeProcessingStatus Status { get; }
        public long AudioTimestampMicroseconds { get; }
        public ListenModeObservationResult? Output { get; }
        public bool WasProcessed => Status == LiveListenModeProcessingStatus.Processed;
    }

    /// <summary>
    /// Latest-utterance/window-wins coordinator for live Listen Mode.
    ///
    /// This coordinator treats each submitted AudioChunk as a complete ASR work item (for example a VAD-delimited
    /// utterance or rolling recognition window). A newer timestamp cancels older work. Even if an ASR/translation
    /// adapter ignores cancellation, the generation gate prevents an older transcript from replacing a newer one.
    /// Continuous microphone buffering and VAD are intentionally outside Core and feed this boundary.
    /// CancellationTokenSource disposal is owned by the SubmitAsync call that created it, so a superseding request
    /// never intentionally disposes a token source while the older adapter may still be unwinding callbacks.
    /// </summary>
    public sealed class LiveListenModeCoordinator : IDisposable
    {
        private readonly ListenModeObservationProcessor processor;
        private readonly object gate = new object();
        private CancellationTokenSource? activeCancellation;
        private long latestAcceptedTimestampMicroseconds = -1;
        private long generation;
        private bool disposed;

        public LiveListenModeCoordinator(ListenModeObservationProcessor processor)
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

        public async Task<LiveListenModeProcessingResult> SubmitAsync(
            AudioChunk audio,
            AssistancePolicy policy,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (audio == null) throw new ArgumentNullException(nameof(audio));
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (audio.TimestampMicroseconds < 0)
                throw new ArgumentOutOfRangeException(nameof(audio), "Audio timestamps must be non-negative.");
            cancellationToken.ThrowIfCancellationRequested();

            CancellationTokenSource localCancellation;
            CancellationTokenSource? previousCancellation;
            long localGeneration;
            lock (gate)
            {
                ThrowIfDisposed();
                if (audio.TimestampMicroseconds <= latestAcceptedTimestampMicroseconds)
                {
                    return new LiveListenModeProcessingResult(
                        LiveListenModeProcessingStatus.StaleInput,
                        audio.TimestampMicroseconds,
                        null);
                }

                latestAcceptedTimestampMicroseconds = audio.TimestampMicroseconds;
                localGeneration = ++generation;
                previousCancellation = activeCancellation;
                localCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                activeCancellation = localCancellation;
            }

            Cancel(previousCancellation);
            var localToken = localCancellation.Token;
            ListenModeObservationResult output;
            try
            {
                output = await processor.ProcessAsync(audio, policy, localToken);
            }
            catch (OperationCanceledException) when (
                localToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return new LiveListenModeProcessingResult(
                    LiveListenModeProcessingStatus.Superseded,
                    audio.TimestampMicroseconds,
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
                    audio.TimestampMicroseconds != latestAcceptedTimestampMicroseconds)
                {
                    return new LiveListenModeProcessingResult(
                        LiveListenModeProcessingStatus.Superseded,
                        audio.TimestampMicroseconds,
                        null);
                }
            }

            return new LiveListenModeProcessingResult(
                LiveListenModeProcessingStatus.Processed,
                audio.TimestampMicroseconds,
                output);
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
            if (cancellation == null) return;
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The owning SubmitAsync completed and disposed the source after it was detached under the lock.
                // In that race there is no remaining operation to cancel.
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(LiveListenModeCoordinator));
        }
    }
}
