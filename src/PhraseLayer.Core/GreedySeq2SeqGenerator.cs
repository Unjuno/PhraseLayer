using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PhraseLayer.Core.Translation
{
    public sealed class Seq2SeqDecoderStepResult
    {
        public Seq2SeqDecoderStepResult(IReadOnlyList<float> logits)
        {
            Logits = logits ?? throw new ArgumentNullException(nameof(logits));
            if (logits.Count == 0)
                throw new ArgumentException("Decoder logits must not be empty.", nameof(logits));
            Logits = logits.ToArray();
        }

        public IReadOnlyList<float> Logits { get; }
    }

    /// <summary>
    /// One encoder result plus one decoder generation session. Implementations may keep encoder outputs and
    /// past-key/value tensors internally; Core only feeds the previously selected token to the next step.
    /// </summary>
    public interface ISeq2SeqGenerationSession : IDisposable
    {
        Task<Seq2SeqDecoderStepResult> DecodeNextAsync(
            int previousTokenId,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    public interface ISeq2SeqGenerationBackend
    {
        Task<ISeq2SeqGenerationSession> StartAsync(
            IReadOnlyList<int> sourceTokenIds,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    /// <summary>
    /// Deterministic correctness-first generation loop for encoder/decoder translation models.
    /// The backend owns platform-specific encoder execution and decoder KV-cache state. This baseline intentionally
    /// supports beamWidth=1 only; beam search should be added only with explicit quality/performance measurements.
    /// </summary>
    public sealed class GreedySeq2SeqTranslationModel : ISeq2SeqTranslationModel
    {
        private readonly ISeq2SeqGenerationBackend backend;
        private readonly int vocabularySize;
        private readonly int decoderStartTokenId;
        private readonly int eosTokenId;
        private readonly HashSet<int> bannedTokenIds;
        private readonly bool forceEosAtMaximumTokens;

        public GreedySeq2SeqTranslationModel(
            ISeq2SeqGenerationBackend backend,
            int vocabularySize,
            int decoderStartTokenId,
            int eosTokenId,
            IEnumerable<int>? bannedTokenIds = null,
            bool forceEosAtMaximumTokens = true)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            if (vocabularySize <= 1) throw new ArgumentOutOfRangeException(nameof(vocabularySize));
            ValidateTokenId(decoderStartTokenId, vocabularySize, nameof(decoderStartTokenId));
            ValidateTokenId(eosTokenId, vocabularySize, nameof(eosTokenId));

            this.vocabularySize = vocabularySize;
            this.decoderStartTokenId = decoderStartTokenId;
            this.eosTokenId = eosTokenId;
            this.bannedTokenIds = new HashSet<int>();
            foreach (var tokenId in bannedTokenIds ?? Array.Empty<int>())
            {
                ValidateTokenId(tokenId, vocabularySize, nameof(bannedTokenIds));
                this.bannedTokenIds.Add(tokenId);
            }
            if (this.bannedTokenIds.Contains(eosTokenId))
                throw new ArgumentException("EOS cannot be a banned generation token.", nameof(bannedTokenIds));
            this.forceEosAtMaximumTokens = forceEosAtMaximumTokens;
        }

        public int VocabularySize => vocabularySize;
        public int DecoderStartTokenId => decoderStartTokenId;
        public int EosTokenId => eosTokenId;
        public bool ForceEosAtMaximumTokens => forceEosAtMaximumTokens;

        public async Task<TranslationGenerationResult> GenerateAsync(
            IReadOnlyList<int> sourceTokenIds,
            TranslationGenerationOptions options,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (sourceTokenIds == null) throw new ArgumentNullException(nameof(sourceTokenIds));
            if (sourceTokenIds.Count == 0)
                throw new ArgumentException("Seq2seq source tokens must not be empty.", nameof(sourceTokenIds));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.BeamWidth != 1)
            {
                throw new NotSupportedException(
                    "The correctness-first seq2seq baseline supports beamWidth=1 only. " +
                    "Add beam search behind a separately tested generator rather than silently approximating it.");
            }
            cancellationToken.ThrowIfCancellationRequested();

            ISeq2SeqGenerationSession? session = null;
            try
            {
                session = await backend.StartAsync(sourceTokenIds, cancellationToken);
                if (session == null)
                    throw new InvalidOperationException("Seq2seq generation backend returned no session.");

                var generated = new List<int>(options.MaximumTargetTokens);
                var previousToken = decoderStartTokenId;

                for (var step = 0; step < options.MaximumTargetTokens; step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var decoded = await session.DecodeNextAsync(previousToken, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (decoded == null)
                        throw new InvalidOperationException("Seq2seq decoder returned no step result.");
                    if (decoded.Logits.Count != vocabularySize)
                    {
                        throw new InvalidOperationException(
                            string.Format(
                                "Seq2seq decoder vocabulary drift: expected {0} logits but received {1}.",
                                vocabularySize,
                                decoded.Logits.Count));
                    }

                    var selected = SelectHighestAllowedToken(decoded.Logits);
                    var isFinalSlot = step == options.MaximumTargetTokens - 1;
                    if (isFinalSlot && selected != eosTokenId && forceEosAtMaximumTokens)
                    {
                        generated.Add(eosTokenId);
                        return new TranslationGenerationResult(
                            generated,
                            TranslationGenerationStopReason.MaximumTokens);
                    }

                    generated.Add(selected);
                    if (selected == eosTokenId)
                    {
                        return new TranslationGenerationResult(
                            generated,
                            TranslationGenerationStopReason.EndOfSequence);
                    }
                    previousToken = selected;
                }

                return new TranslationGenerationResult(
                    generated,
                    TranslationGenerationStopReason.MaximumTokens);
            }
            finally
            {
                session?.Dispose();
            }
        }

        private int SelectHighestAllowedToken(IReadOnlyList<float> logits)
        {
            var selected = -1;
            var selectedLogit = float.NegativeInfinity;
            for (var tokenId = 0; tokenId < logits.Count; tokenId++)
            {
                if (bannedTokenIds.Contains(tokenId)) continue;
                var logit = logits[tokenId];
                if (float.IsNaN(logit) || float.IsInfinity(logit))
                {
                    throw new InvalidOperationException(
                        "Seq2seq decoder produced a non-finite logit at token id " + tokenId + ".");
                }
                if (selected < 0 || logit > selectedLogit)
                {
                    selected = tokenId;
                    selectedLogit = logit;
                }
            }

            if (selected < 0)
                throw new InvalidOperationException("All seq2seq decoder tokens are banned from generation.");
            return selected;
        }

        private static void ValidateTokenId(int tokenId, int vocabularySize, string parameterName)
        {
            if (tokenId < 0 || tokenId >= vocabularySize)
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
