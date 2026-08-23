using System;
using System.Collections.Generic;
using System.Text;

namespace PhraseLayer.Core.Translation
{
    /// <summary>
    /// One NORMAL piece from a measured SentencePiece UNIGRAM model, already mapped through Marian's shared
    /// vocab.json into the token id consumed by the encoder/decoder graph.
    /// </summary>
    public readonly struct SentencePieceUnigramPiece
    {
        public SentencePieceUnigramPiece(string text, double score, int modelTokenId)
        {
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("SentencePiece text is empty.", nameof(text));
            if (double.IsNaN(score) || double.IsInfinity(score)) throw new ArgumentOutOfRangeException(nameof(score));
            if (modelTokenId < 0) throw new ArgumentOutOfRangeException(nameof(modelTokenId));
            Text = text;
            Score = score;
            ModelTokenId = modelTokenId;
        }

        public string Text { get; }
        public double Score { get; }
        public int ModelTokenId { get; }
    }

    /// <summary>
    /// Managed approximation of the measured SentencePiece nmt_nfkc normalizer used by the pinned OPUS-MT
    /// source model. The implementation deliberately remains behind token-exact fixture verification:
    /// .NET FormKC handles Unicode NFKC while the whitespace pass reproduces the measured SentencePiece options
    /// add_dummy_prefix=true, remove_extra_whitespaces=true, escape_whitespaces=true.
    ///
    /// Construction of the official engine must still pass ParityVerifiedTranslationTokenizer.Verify. This class
    /// is therefore not itself evidence that every edge case in SentencePiece's precompiled normalization map is
    /// equivalent to .NET FormKC.
    /// </summary>
    public static class SentencePieceNmtNfkcNormalizer
    {
        public const char WhitespaceMarker = '\u2581';

        public static string NormalizeForEncoding(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            if (text.Length == 0) return string.Empty;

            var compatibility = text.Normalize(NormalizationForm.FormKC);
            var collapsed = new StringBuilder(compatibility.Length + 1);
            var pendingWhitespace = false;

            for (var index = 0; index < compatibility.Length; index++)
            {
                var current = compatibility[index];
                if (char.IsWhiteSpace(current))
                {
                    if (collapsed.Length > 0)
                        pendingWhitespace = true;
                    continue;
                }

                if (pendingWhitespace)
                {
                    collapsed.Append(WhitespaceMarker);
                    pendingWhitespace = false;
                }
                collapsed.Append(current);
            }

            if (collapsed.Length == 0)
                return string.Empty;

            collapsed.Insert(0, WhitespaceMarker);
            return collapsed.ToString();
        }

        public static string DecodePieces(IReadOnlyList<string> pieces)
        {
            if (pieces == null) throw new ArgumentNullException(nameof(pieces));
            if (pieces.Count == 0) return string.Empty;

            var combined = new StringBuilder();
            for (var index = 0; index < pieces.Count; index++)
            {
                var piece = pieces[index];
                if (piece == null) throw new ArgumentException("SentencePiece decode input contains null.", nameof(pieces));
                combined.Append(piece);
            }

            for (var index = 0; index < combined.Length; index++)
            {
                if (combined[index] == WhitespaceMarker)
                    combined[index] = ' ';
            }

            var start = 0;
            while (start < combined.Length && combined[start] == ' ')
                start++;
            return start == 0 ? combined.ToString() : combined.ToString(start, combined.Length - start);
        }
    }

    /// <summary>
    /// Pure-managed SentencePiece UNIGRAM tokenizer for the pinned Marian English->Japanese runtime boundary.
    ///
    /// Encoding performs Viterbi maximization over measured NORMAL piece scores, falls back to the Marian shared
    /// unknown token for an uncovered Unicode scalar, and appends the Marian source EOS token. Decoding maps model
    /// token ids back to target SentencePiece strings and applies the reversible whitespace marker transform.
    ///
    /// The class intentionally accepts already-extracted model data rather than parsing protobuf .spm bytes on
    /// Quest. Local staging converts the pinned tokenizer artifacts into a reviewed runtime manifest. Exact source
    /// fixtures must pass ParityVerifiedTranslationTokenizer before this tokenizer can construct the official NMT
    /// engine.
    /// </summary>
    public sealed class ManagedSentencePieceUnigramTokenizer : ITranslationTokenizer
    {
        private sealed class TrieNode
        {
            public readonly Dictionary<char, TrieNode> Children = new Dictionary<char, TrieNode>();
            public bool HasPiece;
            public double Score;
            public int ModelTokenId;
        }

        private readonly TrieNode root = new TrieNode();
        private readonly IReadOnlyDictionary<int, string> targetPiecesByModelId;
        private readonly int unknownTokenId;
        private readonly int sourceEosTokenId;
        private readonly double unknownScore;

        public ManagedSentencePieceUnigramTokenizer(
            IReadOnlyList<SentencePieceUnigramPiece> sourcePieces,
            IReadOnlyDictionary<int, string> targetPiecesByModelId,
            int unknownTokenId,
            int sourceEosTokenId,
            double unknownScoreOffset = 10.0)
        {
            if (sourcePieces == null) throw new ArgumentNullException(nameof(sourcePieces));
            if (targetPiecesByModelId == null) throw new ArgumentNullException(nameof(targetPiecesByModelId));
            if (sourcePieces.Count == 0) throw new ArgumentException("Source UNIGRAM model contains no NORMAL pieces.", nameof(sourcePieces));
            if (targetPiecesByModelId.Count == 0) throw new ArgumentException("Target vocabulary is empty.", nameof(targetPiecesByModelId));
            if (unknownTokenId < 0) throw new ArgumentOutOfRangeException(nameof(unknownTokenId));
            if (sourceEosTokenId < 0) throw new ArgumentOutOfRangeException(nameof(sourceEosTokenId));
            if (double.IsNaN(unknownScoreOffset) || double.IsInfinity(unknownScoreOffset) || unknownScoreOffset <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(unknownScoreOffset));

            this.targetPiecesByModelId = targetPiecesByModelId;
            this.unknownTokenId = unknownTokenId;
            this.sourceEosTokenId = sourceEosTokenId;

            var minimumScore = double.PositiveInfinity;
            var seenText = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < sourcePieces.Count; index++)
            {
                var piece = sourcePieces[index];
                if (!seenText.Add(piece.Text))
                    throw new ArgumentException("Duplicate SentencePiece NORMAL piece: " + piece.Text, nameof(sourcePieces));
                minimumScore = Math.Min(minimumScore, piece.Score);
                AddPiece(piece);
            }
            unknownScore = minimumScore - unknownScoreOffset;
        }

        public IReadOnlyList<int> Encode(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var normalized = SentencePieceNmtNfkcNormalizer.NormalizeForEncoding(text);
            if (normalized.Length == 0)
                return new[] { sourceEosTokenId };

            var length = normalized.Length;
            var bestScores = new double[length + 1];
            var previous = new int[length + 1];
            var tokenIds = new int[length + 1];
            for (var index = 0; index <= length; index++)
            {
                bestScores[index] = double.NegativeInfinity;
                previous[index] = -1;
                tokenIds[index] = -1;
            }
            bestScores[0] = 0.0;

            for (var start = 0; start < length; start++)
            {
                if (double.IsNegativeInfinity(bestScores[start]))
                    continue;

                var node = root;
                for (var end = start; end < length; end++)
                {
                    TrieNode next;
                    if (!node.Children.TryGetValue(normalized[end], out next))
                        break;
                    node = next;
                    if (!node.HasPiece) continue;
                    UpdateBest(
                        end + 1,
                        start,
                        node.ModelTokenId,
                        bestScores[start] + node.Score,
                        bestScores,
                        previous,
                        tokenIds);
                }

                var unknownEnd = NextUnicodeScalarEnd(normalized, start);
                UpdateBest(
                    unknownEnd,
                    start,
                    unknownTokenId,
                    bestScores[start] + unknownScore,
                    bestScores,
                    previous,
                    tokenIds);
            }

            if (previous[length] < 0)
                throw new InvalidOperationException("SentencePiece Viterbi search could not reach the end of normalized input.");

            var reversed = new List<int>();
            var cursor = length;
            while (cursor > 0)
            {
                var tokenId = tokenIds[cursor];
                var predecessor = previous[cursor];
                if (tokenId < 0 || predecessor < 0 || predecessor >= cursor)
                    throw new InvalidOperationException("SentencePiece Viterbi backpointer graph is invalid.");
                reversed.Add(tokenId);
                cursor = predecessor;
            }

            reversed.Reverse();
            reversed.Add(sourceEosTokenId);
            return reversed;
        }

        public string Decode(IReadOnlyList<int> tokenIds)
        {
            if (tokenIds == null) throw new ArgumentNullException(nameof(tokenIds));
            if (tokenIds.Count == 0) return string.Empty;

            var pieces = new List<string>(tokenIds.Count);
            for (var index = 0; index < tokenIds.Count; index++)
            {
                var tokenId = tokenIds[index];
                if (tokenId == unknownTokenId)
                    throw new InvalidOperationException("Target translation contains the unknown token; refusing to silently manufacture text.");

                string piece;
                if (!targetPiecesByModelId.TryGetValue(tokenId, out piece))
                    throw new InvalidOperationException("Target token id is absent from the measured Marian vocabulary: " + tokenId);
                pieces.Add(piece);
            }
            return SentencePieceNmtNfkcNormalizer.DecodePieces(pieces);
        }

        private void AddPiece(SentencePieceUnigramPiece piece)
        {
            var node = root;
            for (var index = 0; index < piece.Text.Length; index++)
            {
                var current = piece.Text[index];
                TrieNode child;
                if (!node.Children.TryGetValue(current, out child))
                {
                    child = new TrieNode();
                    node.Children.Add(current, child);
                }
                node = child;
            }
            node.HasPiece = true;
            node.Score = piece.Score;
            node.ModelTokenId = piece.ModelTokenId;
        }

        private static void UpdateBest(
            int end,
            int start,
            int tokenId,
            double score,
            double[] bestScores,
            int[] previous,
            int[] tokenIds)
        {
            if (score <= bestScores[end]) return;
            bestScores[end] = score;
            previous[end] = start;
            tokenIds[end] = tokenId;
        }

        private static int NextUnicodeScalarEnd(string text, int start)
        {
            if (start < 0 || start >= text.Length) throw new ArgumentOutOfRangeException(nameof(start));
            if (char.IsHighSurrogate(text[start]) &&
                start + 1 < text.Length &&
                char.IsLowSurrogate(text[start + 1]))
            {
                return start + 2;
            }
            return start + 1;
        }
    }
}
