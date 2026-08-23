using System;
using System.Collections.Generic;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class ManagedSentencePieceTokenizerTests
    {
        [Fact]
        public void NmtNfkcNormalizerAppliesCompatibilityAndMeasuredWhitespaceRules()
        {
            var normalized = SentencePieceNmtNfkcNormalizer.NormalizeForEncoding("  Ａ\tB   C  ");

            Assert.Equal("▁A▁B▁C", normalized);
        }

        [Fact]
        public void ViterbiChoosesHigherTotalUnigramScore()
        {
            var tokenizer = CreateTokenizer(
                new SentencePieceUnigramPiece("▁", -4.0, 6),
                new SentencePieceUnigramPiece("a", -4.0, 7),
                new SentencePieceUnigramPiece("b", -1.0, 8),
                new SentencePieceUnigramPiece("▁a", -1.0, 9),
                new SentencePieceUnigramPiece("▁ab", -3.0, 10));

            var encoded = tokenizer.Encode("ab");

            Assert.Equal(new[] { 9, 8, 0 }, encoded);
        }

        [Fact]
        public void SourceEncodingAppendsMarianEos()
        {
            var tokenizer = CreateTokenizer(
                new SentencePieceUnigramPiece("▁hello", -1.0, 42));

            Assert.Equal(new[] { 42, 0 }, tokenizer.Encode("hello"));
        }

        [Fact]
        public void UncoveredUnicodeScalarFallsBackToSingleUnknownToken()
        {
            var tokenizer = CreateTokenizer(
                new SentencePieceUnigramPiece("▁", -1.0, 6));

            var encoded = tokenizer.Encode("😀");

            Assert.Equal(new[] { 6, 1, 0 }, encoded);
        }

        [Fact]
        public void DecodeUsesMeasuredTargetPieceMapAndWhitespaceMarker()
        {
            var tokenizer = new ManagedSentencePieceUnigramTokenizer(
                new[] { new SentencePieceUnigramPiece("▁x", -1.0, 2) },
                new Dictionary<int, string>
                {
                    { 100, "▁疲れ" },
                    { 101, "て" },
                    { 102, "▁いた" },
                },
                unknownTokenId: 1,
                sourceEosTokenId: 0);

            Assert.Equal("疲れて いた", tokenizer.Decode(new[] { 100, 101, 102 }));
        }

        [Fact]
        public void DecodeFailsClosedOnUnknownTargetToken()
        {
            var tokenizer = CreateTokenizer(
                new SentencePieceUnigramPiece("▁hello", -1.0, 42));

            var error = Assert.Throws<InvalidOperationException>(() => tokenizer.Decode(new[] { 1 }));

            Assert.Contains("unknown token", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DuplicateSourcePiecesAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new ManagedSentencePieceUnigramTokenizer(
                new[]
                {
                    new SentencePieceUnigramPiece("▁x", -1.0, 2),
                    new SentencePieceUnigramPiece("▁x", -2.0, 3),
                },
                TargetPieces(),
                unknownTokenId: 1,
                sourceEosTokenId: 0));
        }

        [Fact]
        public void EmptySourceProducesOnlyEos()
        {
            var tokenizer = CreateTokenizer(
                new SentencePieceUnigramPiece("▁hello", -1.0, 42));

            Assert.Equal(new[] { 0 }, tokenizer.Encode(string.Empty));
            Assert.Equal(new[] { 0 }, tokenizer.Encode("   \t"));
        }

        private static ManagedSentencePieceUnigramTokenizer CreateTokenizer(
            params SentencePieceUnigramPiece[] pieces)
        {
            return new ManagedSentencePieceUnigramTokenizer(
                pieces,
                TargetPieces(),
                unknownTokenId: 1,
                sourceEosTokenId: 0);
        }

        private static IReadOnlyDictionary<int, string> TargetPieces()
        {
            return new Dictionary<int, string>
            {
                { 2, "▁dummy" },
                { 42, "▁hello" },
            };
        }
    }
}
