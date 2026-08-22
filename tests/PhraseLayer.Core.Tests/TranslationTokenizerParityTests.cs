using System;
using System.Collections.Generic;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class TranslationTokenizerParityTests
    {
        [Fact]
        public void ExactTokenizerPassesEncodeAndDecodeFixtures()
        {
            var tokenizer = new FixtureTokenizer(
                new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
                {
                    { "hello", new[] { 4, 7, 0 } },
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "11,12,13", "こんにちは" },
                });
            var fixtures = CreateFixtures();

            var report = TranslationTokenizerParityGate.ValidateAndBuildReport(tokenizer, fixtures);

            Assert.Contains("parity=exact", report);
            Assert.Contains("encode_fixtures=1", report);
            Assert.Contains("decode_fixtures=1", report);
        }

        [Fact]
        public void SingleEncodeTokenDifferenceFailsClosed()
        {
            var tokenizer = new FixtureTokenizer(
                new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
                {
                    { "hello", new[] { 4, 8, 0 } },
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "11,12,13", "こんにちは" },
                });

            var error = Assert.Throws<InvalidOperationException>(() =>
                TranslationTokenizerParityGate.ValidateAndBuildReport(tokenizer, CreateFixtures()));

            Assert.Contains("token mismatch at index 1", error.Message);
        }

        [Fact]
        public void MerelyPlausibleDecodeTextIsRejected()
        {
            var tokenizer = new FixtureTokenizer(
                new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
                {
                    { "hello", new[] { 4, 7, 0 } },
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "11,12,13", "今日は" },
                });

            var error = Assert.Throws<InvalidOperationException>(() =>
                TranslationTokenizerParityGate.ValidateAndBuildReport(tokenizer, CreateFixtures()));

            Assert.Contains("decode fixture 0 mismatch", error.Message);
        }

        private static TranslationTokenizerFixtureSet CreateFixtures()
        {
            return new TranslationTokenizerFixtureSet(
                new[]
                {
                    new TranslationTokenizerEncodeFixture("hello", new[] { 4, 7, 0 }),
                },
                new[]
                {
                    new TranslationTokenizerDecodeFixture(new[] { 11, 12, 13 }, "こんにちは"),
                });
        }

        private sealed class FixtureTokenizer : ITranslationTokenizer
        {
            private readonly IReadOnlyDictionary<string, IReadOnlyList<int>> encodings;
            private readonly IReadOnlyDictionary<string, string> decodings;

            public FixtureTokenizer(
                IReadOnlyDictionary<string, IReadOnlyList<int>> encodings,
                IReadOnlyDictionary<string, string> decodings)
            {
                this.encodings = encodings;
                this.decodings = decodings;
            }

            public IReadOnlyList<int> Encode(string text)
            {
                IReadOnlyList<int>? result;
                if (!encodings.TryGetValue(text, out result) || result == null)
                    throw new InvalidOperationException("Unknown fixture input: " + text);
                return result;
            }

            public string Decode(IReadOnlyList<int> tokenIds)
            {
                var key = string.Join(",", tokenIds);
                string? result;
                if (!decodings.TryGetValue(key, out result) || result == null)
                    throw new InvalidOperationException("Unknown fixture token sequence: " + key);
                return result;
            }
        }
    }
}
