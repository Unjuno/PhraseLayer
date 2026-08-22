using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PhraseLayer.Core.Translation
{
    /// <summary>
    /// Generation contract copied from the revision-pinned Helsinki-NLP/opus-mt-en-jap configuration.
    /// Keeping these values in Core makes the reference search policy testable without Unity, ONNX, or
    /// SentencePiece implementations.
    /// </summary>
    public static class OpusMtEnJapGenerationContract
    {
        public const int BosTokenId = 0;
        public const int DecoderStartTokenId = 46275;
        public const int EosTokenId = 0;
        public const int ForcedEosTokenId = 0;
        public const int PadTokenId = 46275;
        public const int MaxLength = 512;
        public const int BeamWidth = 4;
        public const double LengthPenalty = 1.0;

        public static TranslationGenerationOptions CreateReferenceOptions()
        {
            return new TranslationGenerationOptions(
                decoderStartTokenId: DecoderStartTokenId,
                eosTokenId: EosTokenId,
                padTokenId: PadTokenId,
                maxLength: MaxLength,
                beamWidth: BeamWidth,
                lengthPenalty: LengthPenalty);
        }

        public static IAutoregressiveTranslationBackend WrapBackend(
            IAutoregressiveTranslationBackend backend)
        {
            return new ForcedEosTranslationBackend(
                backend,
                ForcedEosTokenId,
                MaxLength);
        }

        public static AutoregressiveTranslationEngine CreateReferenceEngine(
            ITranslationTokenizer tokenizer,
            IAutoregressiveTranslationBackend backend)
        {
            if (tokenizer == null) throw new ArgumentNullException(nameof(tokenizer));
            if (backend == null) throw new ArgumentNullException(nameof(backend));
            return new AutoregressiveTranslationEngine(
                tokenizer,
                WrapBackend(backend),
                CreateReferenceOptions());
        }
    }

    /// <summary>
    /// Applies a model generation-config forced-EOS rule at the backend boundary.
    ///
    /// Transformers' forced_eos_token_id makes the final decoder slot EOS when max_length is reached.
    /// The generic search engine intentionally does not assume that every seq2seq model has this rule,
    /// so model-specific contracts wrap their backend instead. The forced token is assigned log-probability
    /// zero because it is the only admissible candidate at that step after the logits processor is applied.
    /// </summary>
    public sealed class ForcedEosTranslationBackend : IAutoregressiveTranslationBackend
    {
        private readonly IAutoregressiveTranslationBackend inner;
        private readonly int forcedEosTokenId;
        private readonly int maxLength;
        private static readonly Task<IReadOnlyList<TranslationTokenCandidate>> EmptyTask =
            Task.FromResult<IReadOnlyList<TranslationTokenCandidate>>(Array.Empty<TranslationTokenCandidate>());

        public ForcedEosTranslationBackend(
            IAutoregressiveTranslationBackend inner,
            int forcedEosTokenId,
            int maxLength)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (forcedEosTokenId < 0) throw new ArgumentOutOfRangeException(nameof(forcedEosTokenId));
            if (maxLength < 2) throw new ArgumentOutOfRangeException(nameof(maxLength));
            this.forcedEosTokenId = forcedEosTokenId;
            this.maxLength = maxLength;
        }

        public Task<IReadOnlyList<TranslationTokenCandidate>> PredictNextAsync(
            IReadOnlyList<int> sourceTokenIds,
            IReadOnlyList<int> generatedTokenIds,
            int maxCandidates,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (sourceTokenIds == null) throw new ArgumentNullException(nameof(sourceTokenIds));
            if (generatedTokenIds == null) throw new ArgumentNullException(nameof(generatedTokenIds));
            if (maxCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(maxCandidates));
            cancellationToken.ThrowIfCancellationRequested();

            // generatedTokenIds already includes decoder_start_token_id. If appending one more token would
            // fill max_length, forced_eos_token_id is the only legal next token.
            if (generatedTokenIds.Count >= maxLength - 1)
            {
                IReadOnlyList<TranslationTokenCandidate> forced = new[]
                {
                    new TranslationTokenCandidate(forcedEosTokenId, 0.0)
                };
                return Task.FromResult(forced);
            }

            return inner.PredictNextAsync(
                sourceTokenIds,
                generatedTokenIds,
                maxCandidates,
                cancellationToken) ?? EmptyTask;
        }
    }
}
