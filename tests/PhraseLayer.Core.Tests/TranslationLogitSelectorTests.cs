using System;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class TranslationLogitSelectorTests
    {
        [Fact]
        public void SelectsTopCandidatesAndReturnsNormalizedLogProbabilities()
        {
            var result = TranslationLogitSelector.SelectTopLogProbabilities(
                new[] { 0.0f, 2.0f, 1.0f, -1.0f },
                offset: 0,
                vocabularySize: 4,
                maxCandidates: 2);

            Assert.Equal(2, result.Count);
            Assert.Equal(1, result[0].TokenId);
            Assert.Equal(2, result[1].TokenId);

            var normalizer = Math.Log(Math.Exp(0.0) + Math.Exp(2.0) + Math.Exp(1.0) + Math.Exp(-1.0));
            Assert.Equal(2.0 - normalizer, result[0].LogProbability, 10);
            Assert.Equal(1.0 - normalizer, result[1].LogProbability, 10);
        }

        [Fact]
        public void UsesTokenIdAsDeterministicTieBreaker()
        {
            var result = TranslationLogitSelector.SelectTopLogProbabilities(
                new[] { 3.0f, 1.0f, 3.0f },
                offset: 0,
                vocabularySize: 3,
                maxCandidates: 2);

            Assert.Equal(0, result[0].TokenId);
            Assert.Equal(2, result[1].TokenId);
        }

        [Fact]
        public void SupportsSelectingLastDecoderPositionFromFlatTensor()
        {
            var result = TranslationLogitSelector.SelectTopLogProbabilities(
                new[] { 100.0f, 99.0f, -2.0f, 4.0f, 1.0f, 3.0f },
                offset: 3,
                vocabularySize: 3,
                maxCandidates: 1);

            Assert.Single(result);
            Assert.Equal(0, result[0].TokenId);
        }

        [Fact]
        public void RejectsNonFiniteLogits()
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                TranslationLogitSelector.SelectTopLogProbabilities(
                    new[] { 0.0f, float.NaN },
                    offset: 0,
                    vocabularySize: 2,
                    maxCandidates: 1));

            Assert.Contains("non-finite", error.Message);
        }
    }
}
