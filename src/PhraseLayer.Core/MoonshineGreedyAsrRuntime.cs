using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Inputs;

namespace PhraseLayer.Core.Audio
{
    public sealed class AsrDecoderStepResult
    {
        public AsrDecoderStepResult(IReadOnlyList<float> logits)
        {
            if (logits == null) throw new ArgumentNullException(nameof(logits));
            if (logits.Count == 0) throw new ArgumentException("ASR decoder logits must not be empty.", nameof(logits));
            Logits = logits.ToArray();
        }

        public IReadOnlyList<float> Logits { get; }
    }

    /// <summary>
    /// One encoded audio item plus one decoder-cache session. Concrete implementations may retain
    /// encoder outputs and past key/value tensors on a device; Core only supplies the previously chosen token.
    /// </summary>
    public interface IAudioSeq2SeqGenerationSession : IDisposable
    {
        Task<AsrDecoderStepResult> DecodeNextAsync(
            int previousTokenId,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    public interface IAudioSeq2SeqGenerationBackend
    {
        Task<IAudioSeq2SeqGenerationSession> StartAsync(
            AudioChunk monoAudio,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    public interface IAsrTokenDecoder
    {
        string Decode(IReadOnlyList<int> tokenIds);
    }

    /// <summary>
    /// Deterministic generation diagnostics used by parity/integration gates. Production callers can continue
    /// using IOfflineAsrRuntime.TranscribePreparedAsync and never depend on token-level Moonshine details.
    /// </summary>
    public sealed class MoonshineGreedyDecodeTrace
    {
        public MoonshineGreedyDecodeTrace(
            AsrObservation observation,
            IReadOnlyList<int> tokenIds,
            bool terminatedByEos,
            int decoderSteps)
        {
            Observation = observation ?? throw new ArgumentNullException(nameof(observation));
            if (tokenIds == null) throw new ArgumentNullException(nameof(tokenIds));
            if (decoderSteps < 0) throw new ArgumentOutOfRangeException(nameof(decoderSteps));
            if (decoderSteps < tokenIds.Count)
                throw new ArgumentException("Decoder step count cannot be smaller than emitted token count.", nameof(decoderSteps));

            TokenIds = tokenIds.ToArray();
            TerminatedByEos = terminatedByEos;
            DecoderSteps = decoderSteps;
        }

        public AsrObservation Observation { get; }
        public IReadOnlyList<int> TokenIds { get; }
        public bool TerminatedByEos { get; }
        public int DecoderSteps { get; }
    }

    /// <summary>
    /// Correctness-first Moonshine Tiny runtime. Input preparation to 16 kHz is owned by OfflineAsrEngine;
    /// this runtime validates that boundary, executes deterministic greedy decoding, stops at EOS, and turns
    /// generated token ids into the final transcript through a replaceable tokenizer adapter.
    ///
    /// Beam search/sampling and streaming partial hypotheses are deliberately not approximated here. They can be
    /// added behind separate, measured implementations without changing IAsrEngine or Listen Mode Core.
    /// </summary>
    public sealed class MoonshineGreedyAsrRuntime : IOfflineAsrRuntime
    {
        private readonly IAudioSeq2SeqGenerationBackend backend;
        private readonly IAsrTokenDecoder tokenDecoder;
        private readonly int maximumGenerationLength;

        public MoonshineGreedyAsrRuntime(
            IAudioSeq2SeqGenerationBackend backend,
            IAsrTokenDecoder tokenDecoder,
            int maximumGenerationLength = MoonshineTinyAsrContract.MaximumGenerationLength)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            this.tokenDecoder = tokenDecoder ?? throw new ArgumentNullException(nameof(tokenDecoder));
            if (maximumGenerationLength <= 0 || maximumGenerationLength > MoonshineTinyAsrContract.MaximumGenerationLength)
                throw new ArgumentOutOfRangeException(nameof(maximumGenerationLength));
            this.maximumGenerationLength = maximumGenerationLength;
        }

        public int RequiredSampleRate => MoonshineTinyAsrContract.RequiredSampleRate;
        public int MaximumGenerationLength => maximumGenerationLength;

        public async Task<AsrObservation> TranscribePreparedAsync(
            AudioChunk monoAudio,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var trace = await TranscribePreparedWithTraceAsync(monoAudio, cancellationToken);
            return trace.Observation;
        }

        public async Task<MoonshineGreedyDecodeTrace> TranscribePreparedWithTraceAsync(
            AudioChunk monoAudio,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (monoAudio == null) throw new ArgumentNullException(nameof(monoAudio));
            if (monoAudio.SampleRate != RequiredSampleRate)
                throw new ArgumentException("Moonshine Tiny requires prepared 16 kHz mono audio.", nameof(monoAudio));
            cancellationToken.ThrowIfCancellationRequested();

            IAudioSeq2SeqGenerationSession? session = null;
            try
            {
                session = await backend.StartAsync(monoAudio, cancellationToken);
                if (session == null) throw new InvalidOperationException("Moonshine backend returned no generation session.");

                var generated = new List<int>(maximumGenerationLength);
                var previousToken = MoonshineTinyAsrContract.DecoderStartTokenId;
                var terminatedByEos = false;
                var decoderSteps = 0;
                for (var step = 0; step < maximumGenerationLength; step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var decoded = await session.DecodeNextAsync(previousToken, cancellationToken);
                    decoderSteps++;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (decoded == null) throw new InvalidOperationException("Moonshine decoder returned no step result.");
                    if (decoded.Logits.Count != MoonshineTinyAsrContract.VocabularySize)
                    {
                        throw new InvalidOperationException(
                            string.Format(
                                "Moonshine decoder vocabulary drift: expected {0} logits but received {1}.",
                                MoonshineTinyAsrContract.VocabularySize,
                                decoded.Logits.Count));
                    }

                    var selected = SelectHighestFiniteToken(decoded.Logits);
                    if (selected == MoonshineTinyAsrContract.EosTokenId)
                    {
                        terminatedByEos = true;
                        break;
                    }
                    generated.Add(selected);
                    previousToken = selected;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var transcript = tokenDecoder.Decode(generated) ?? string.Empty;
                var observation = new AsrObservation(transcript.Trim(), isFinal: true);
                return new MoonshineGreedyDecodeTrace(
                    observation,
                    generated,
                    terminatedByEos,
                    decoderSteps);
            }
            finally
            {
                session?.Dispose();
            }
        }

        private static int SelectHighestFiniteToken(IReadOnlyList<float> logits)
        {
            var selected = -1;
            var selectedLogit = float.NegativeInfinity;
            for (var tokenId = 0; tokenId < logits.Count; tokenId++)
            {
                var logit = logits[tokenId];
                if (float.IsNaN(logit) || float.IsInfinity(logit))
                    throw new InvalidOperationException("Moonshine decoder produced a non-finite logit at token id " + tokenId + ".");
                if (selected < 0 || logit > selectedLogit)
                {
                    selected = tokenId;
                    selectedLogit = logit;
                }
            }
            if (selected < 0) throw new InvalidOperationException("Moonshine decoder produced no selectable token.");
            return selected;
        }
    }
}
