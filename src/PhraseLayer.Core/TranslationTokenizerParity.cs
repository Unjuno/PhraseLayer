using System;
using System.Collections.Generic;

namespace PhraseLayer.Core.Translation
{
    public sealed class TranslationTokenizerEncodeFixture
    {
        public TranslationTokenizerEncodeFixture(string sourceText, IReadOnlyList<int> expectedTokenIds)
        {
            SourceText = sourceText ?? throw new ArgumentNullException(nameof(sourceText));
            ExpectedTokenIds = expectedTokenIds ?? throw new ArgumentNullException(nameof(expectedTokenIds));
        }

        public string SourceText { get; }
        public IReadOnlyList<int> ExpectedTokenIds { get; }
    }

    public sealed class TranslationTokenizerDecodeFixture
    {
        public TranslationTokenizerDecodeFixture(IReadOnlyList<int> tokenIds, string expectedText)
        {
            TokenIds = tokenIds ?? throw new ArgumentNullException(nameof(tokenIds));
            ExpectedText = expectedText ?? throw new ArgumentNullException(nameof(expectedText));
        }

        public IReadOnlyList<int> TokenIds { get; }
        public string ExpectedText { get; }
    }

    public sealed class TranslationTokenizerFixtureSet
    {
        public TranslationTokenizerFixtureSet(
            IReadOnlyList<TranslationTokenizerEncodeFixture> encodeFixtures,
            IReadOnlyList<TranslationTokenizerDecodeFixture> decodeFixtures)
        {
            EncodeFixtures = encodeFixtures ?? throw new ArgumentNullException(nameof(encodeFixtures));
            DecodeFixtures = decodeFixtures ?? throw new ArgumentNullException(nameof(decodeFixtures));
            if (EncodeFixtures.Count == 0) throw new ArgumentException("At least one tokenizer encode fixture is required.", nameof(encodeFixtures));
            if (DecodeFixtures.Count == 0) throw new ArgumentException("At least one tokenizer decode fixture is required.", nameof(decodeFixtures));
        }

        public IReadOnlyList<TranslationTokenizerEncodeFixture> EncodeFixtures { get; }
        public IReadOnlyList<TranslationTokenizerDecodeFixture> DecodeFixtures { get; }
    }

    /// <summary>
    /// Fail-closed token-exact boundary for local translation tokenizers.
    ///
    /// Marian/SentencePiece is not interchangeable with a whitespace/BPE approximation. A tokenizer implementation
    /// can enter the production translation pipeline only after every revision-pinned encode/decode fixture matches
    /// exactly. This prevents a tokenizer that merely produces plausible text from silently changing model inputs.
    /// </summary>
    public static class TranslationTokenizerParityGate
    {
        public static string ValidateAndBuildReport(
            ITranslationTokenizer tokenizer,
            TranslationTokenizerFixtureSet fixtures)
        {
            if (tokenizer == null) throw new ArgumentNullException(nameof(tokenizer));
            if (fixtures == null) throw new ArgumentNullException(nameof(fixtures));

            for (var fixtureIndex = 0; fixtureIndex < fixtures.EncodeFixtures.Count; fixtureIndex++)
            {
                var fixture = fixtures.EncodeFixtures[fixtureIndex];
                if (fixture == null)
                    throw new InvalidOperationException("Tokenizer encode fixture is null at index " + fixtureIndex + ".");

                var actual = tokenizer.Encode(fixture.SourceText);
                if (actual == null)
                    throw new InvalidOperationException("Tokenizer returned null for encode fixture " + fixtureIndex + ".");
                AssertTokenSequence(fixture.ExpectedTokenIds, actual, "encode fixture " + fixtureIndex);
            }

            for (var fixtureIndex = 0; fixtureIndex < fixtures.DecodeFixtures.Count; fixtureIndex++)
            {
                var fixture = fixtures.DecodeFixtures[fixtureIndex];
                if (fixture == null)
                    throw new InvalidOperationException("Tokenizer decode fixture is null at index " + fixtureIndex + ".");

                var actual = tokenizer.Decode(fixture.TokenIds);
                if (!string.Equals(actual, fixture.ExpectedText, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Tokenizer decode fixture " + fixtureIndex + " mismatch. Expected '" +
                        fixture.ExpectedText + "' actual '" + actual + "'.");
                }
            }

            return
                "translation tokenizer parity=exact" +
                " encode_fixtures=" + fixtures.EncodeFixtures.Count +
                " decode_fixtures=" + fixtures.DecodeFixtures.Count;
        }

        private static void AssertTokenSequence(
            IReadOnlyList<int> expected,
            IReadOnlyList<int> actual,
            string label)
        {
            if (expected.Count != actual.Count)
            {
                throw new InvalidOperationException(
                    "Tokenizer " + label + " length mismatch: expected " + expected.Count +
                    " actual " + actual.Count + ".");
            }

            for (var index = 0; index < expected.Count; index++)
            {
                if (expected[index] == actual[index]) continue;
                throw new InvalidOperationException(
                    "Tokenizer " + label + " token mismatch at index " + index +
                    ": expected " + expected[index] + " actual " + actual[index] + ".");
            }
        }
    }
}
