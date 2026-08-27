using System;
using System.Collections.Generic;
using System.Linq;

namespace PhraseLayer.Core.Translation
{
    /// <summary>
    /// Minimal platform-neutral SentencePiece boundary. A real implementation may be managed or native,
    /// but it must apply the normalization and segmentation encoded in the exact .spm model.
    /// </summary>
    public interface ISentencePieceProcessor
    {
        IReadOnlyList<string> EncodePieces(string text);
        string DecodePieces(IReadOnlyList<string> pieces);
        bool TryGetPiece(int sentencePieceId, out string piece);
    }

    /// <summary>
    /// Marian does not assume that SentencePiece internal ids equal model vocabulary ids. Pieces are first
    /// produced by source.spm and then mapped through vocab.json. This mirrors the critical MarianTokenizer
    /// boundary and prevents accidental use of unrelated raw SentencePiece ids.
    /// </summary>
    public sealed class MarianSentencePieceTokenizer : ITranslationTokenizer
    {
        private readonly ISentencePieceProcessor sourceProcessor;
        private readonly ISentencePieceProcessor targetProcessor;
        private readonly IReadOnlyDictionary<string, int> encoder;
        private readonly IReadOnlyDictionary<int, string> decoder;
        private readonly HashSet<int> skippedTargetTokenIds;
        private readonly int unknownTokenId;
        private readonly int eosTokenId;
        private readonly int padTokenId;

        public MarianSentencePieceTokenizer(
            ISentencePieceProcessor sourceProcessor,
            ISentencePieceProcessor targetProcessor,
            IReadOnlyDictionary<string, int> vocabulary,
            int eosTokenId = OpusMtEnJaMarianContract.ExpectedEosTokenId,
            int padTokenId = OpusMtEnJaMarianContract.ExpectedPadTokenId)
        {
            this.sourceProcessor = sourceProcessor ?? throw new ArgumentNullException(nameof(sourceProcessor));
            this.targetProcessor = targetProcessor ?? throw new ArgumentNullException(nameof(targetProcessor));
            if (vocabulary == null) throw new ArgumentNullException(nameof(vocabulary));
            if (vocabulary.Count == 0) throw new ArgumentException("Marian vocabulary must not be empty.", nameof(vocabulary));

            var copiedEncoder = new Dictionary<string, int>(vocabulary.Count, StringComparer.Ordinal);
            var copiedDecoder = new Dictionary<int, string>(vocabulary.Count);
            foreach (var entry in vocabulary)
            {
                if (entry.Key == null)
                    throw new ArgumentException("Marian vocabulary contains a null piece.", nameof(vocabulary));
                if (entry.Value < 0)
                    throw new ArgumentException("Marian vocabulary ids must be non-negative.", nameof(vocabulary));
                if (copiedEncoder.ContainsKey(entry.Key))
                    throw new ArgumentException("Marian vocabulary contains a duplicate piece.", nameof(vocabulary));
                if (copiedDecoder.ContainsKey(entry.Value))
                    throw new ArgumentException("Marian vocabulary contains a duplicate token id.", nameof(vocabulary));
                copiedEncoder.Add(entry.Key, entry.Value);
                copiedDecoder.Add(entry.Value, entry.Key);
            }

            encoder = copiedEncoder;
            decoder = copiedDecoder;
            unknownTokenId = RequireVocabularyToken(copiedEncoder, "<unk>", nameof(vocabulary));
            this.eosTokenId = eosTokenId;
            this.padTokenId = padTokenId;
            RequireVocabularyId(copiedDecoder, eosTokenId, "EOS", nameof(vocabulary));
            RequireVocabularyId(copiedDecoder, padTokenId, "PAD", nameof(vocabulary));

            if (!string.Equals(copiedDecoder[eosTokenId], "</s>", StringComparison.Ordinal))
                throw new ArgumentException("Configured Marian EOS id must resolve to </s>.", nameof(vocabulary));
            if (!string.Equals(copiedDecoder[padTokenId], "<pad>", StringComparison.Ordinal))
                throw new ArgumentException("Configured Marian PAD id must resolve to <pad>.", nameof(vocabulary));

            skippedTargetTokenIds = new HashSet<int> { eosTokenId, padTokenId };
            AddSpecialIfPresent(copiedEncoder, skippedTargetTokenIds, "<eop>");
            AddSpecialIfPresent(copiedEncoder, skippedTargetTokenIds, "<eod>");
        }

        public int UnknownTokenId => unknownTokenId;
        public int EosTokenId => eosTokenId;
        public int PadTokenId => padTokenId;

        public TranslationTokenSequence EncodeSource(string sourceText, int maximumTokens)
        {
            if (sourceText == null) throw new ArgumentNullException(nameof(sourceText));
            if (maximumTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maximumTokens));
            if (maximumTokens < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumTokens),
                    "Marian source sequences require room for at least one piece plus EOS.");
            }

            var pieces = sourceProcessor.EncodePieces(sourceText)
                ?? throw new InvalidOperationException("SentencePiece source processor returned no piece sequence.");
            if (pieces.Count == 0)
                throw new InvalidOperationException("SentencePiece source processor returned an empty piece sequence.");

            var pieceBudget = maximumTokens - 1;
            var usedPieceCount = Math.Min(pieceBudget, pieces.Count);
            var ids = new int[usedPieceCount + 1];
            for (var index = 0; index < usedPieceCount; index++)
            {
                var piece = pieces[index];
                if (piece == null)
                    throw new InvalidOperationException("SentencePiece source processor returned a null piece.");
                ids[index] = encoder.TryGetValue(piece, out var tokenId)
                    ? tokenId
                    : unknownTokenId;
            }
            ids[usedPieceCount] = eosTokenId;

            return new TranslationTokenSequence(ids, pieces.Count > pieceBudget);
        }

        public string DecodeTarget(IReadOnlyList<int> targetTokenIds)
        {
            if (targetTokenIds == null) throw new ArgumentNullException(nameof(targetTokenIds));
            if (targetTokenIds.Count == 0)
                throw new ArgumentException("Target token ids must not be empty.", nameof(targetTokenIds));

            var pieces = new List<string>(targetTokenIds.Count);
            for (var index = 0; index < targetTokenIds.Count; index++)
            {
                var tokenId = targetTokenIds[index];
                if (skippedTargetTokenIds.Contains(tokenId)) continue;

                if (decoder.TryGetValue(tokenId, out var vocabularyPiece))
                {
                    pieces.Add(vocabularyPiece);
                    continue;
                }

                if (targetProcessor.TryGetPiece(tokenId, out var sentencePiece) && !string.IsNullOrEmpty(sentencePiece))
                {
                    pieces.Add(sentencePiece);
                    continue;
                }

                pieces.Add("<unk>");
            }

            if (pieces.Count == 0) return string.Empty;
            var decoded = targetProcessor.DecodePieces(pieces);
            if (decoded == null)
                throw new InvalidOperationException("SentencePiece target processor returned null while decoding pieces.");
            return decoded.Trim();
        }

        private static int RequireVocabularyToken(
            IReadOnlyDictionary<string, int> vocabulary,
            string token,
            string parameterName)
        {
            if (!vocabulary.TryGetValue(token, out var id))
                throw new ArgumentException("Marian vocabulary is missing required token " + token + ".", parameterName);
            return id;
        }

        private static void RequireVocabularyId(
            IReadOnlyDictionary<int, string> decoder,
            int id,
            string label,
            string parameterName)
        {
            if (id < 0 || !decoder.ContainsKey(id))
                throw new ArgumentException("Marian vocabulary is missing configured " + label + " id " + id + ".", parameterName);
        }

        private static void AddSpecialIfPresent(
            IReadOnlyDictionary<string, int> vocabulary,
            ISet<int> output,
            string token)
        {
            if (vocabulary.TryGetValue(token, out var id)) output.Add(id);
        }
    }
}
