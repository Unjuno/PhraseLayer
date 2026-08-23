using System;
using PhraseLayer.Core.Translation;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class TranslationQualityGateTests
    {
        [Fact]
        public void CompletePassingReviewPassesCandidatePromotion()
        {
            var cases = new[]
            {
                Case("negation", "Do not enter.", TranslationQualityDimension.NegationPolarity),
                Case("mwe", "Please keep off the grass.", TranslationQualityDimension.MultiwordExpression),
            };
            var reviews = new[]
            {
                Pass("negation", "入らないでください。"),
                Pass("mwe", "芝生に入らないでください。"),
            };

            var summary = TranslationQualityGate.Evaluate(cases, reviews, TranslationQualityPolicy.CandidatePromotion);

            Assert.True(summary.Passed);
            Assert.Equal(2, summary.ReviewedCases);
            Assert.Equal(0, summary.CriticalFailures);
            Assert.Empty(summary.MissingCaseIds);
        }

        [Fact]
        public void CriticalPolarityFailureBlocksPromotion()
        {
            var cases = new[]
            {
                Case("negation", "Do not enter.", TranslationQualityDimension.NegationPolarity),
            };
            var reviews = new[]
            {
                new TranslationQualityReview(
                    "negation",
                    "入ってください。",
                    TranslationQualitySeverity.Critical,
                    new[] { TranslationQualityDimension.NegationPolarity, TranslationQualityDimension.Adequacy },
                    "Negation was reversed."),
            };

            var summary = TranslationQualityGate.Evaluate(cases, reviews, TranslationQualityPolicy.CandidatePromotion);

            Assert.False(summary.Passed);
            Assert.Equal(1, summary.CriticalFailures);
            Assert.Equal(1.0, summary.MajorOrWorseRate, 6);
            Assert.Equal(1, summary.FailedDimensionCounts[TranslationQualityDimension.NegationPolarity]);
        }

        [Fact]
        public void IncompleteReviewBlocksStrictPromotion()
        {
            var cases = new[]
            {
                Case("a", "I am ready.", TranslationQualityDimension.Adequacy),
                Case("b", "I am still waiting.", TranslationQualityDimension.TemporalAspect),
            };
            var reviews = new[]
            {
                Pass("a", "準備できています。"),
            };

            var summary = TranslationQualityGate.Evaluate(cases, reviews, TranslationQualityPolicy.CandidatePromotion);

            Assert.False(summary.Passed);
            Assert.Single(summary.MissingCaseIds);
            Assert.Equal("b", summary.MissingCaseIds[0]);
        }

        [Fact]
        public void MajorFailureRateIsExplicitlyBounded()
        {
            var cases = new[]
            {
                Case("a", "one", TranslationQualityDimension.Adequacy),
                Case("b", "two", TranslationQualityDimension.Adequacy),
                Case("c", "three", TranslationQualityDimension.Adequacy),
                Case("d", "four", TranslationQualityDimension.Adequacy),
            };
            var reviews = new[]
            {
                new TranslationQualityReview(
                    "a",
                    "誤訳",
                    TranslationQualitySeverity.Major,
                    new[] { TranslationQualityDimension.Adequacy },
                    "Meaning materially changed."),
                Pass("b", "二"),
                Pass("c", "三"),
                Pass("d", "四"),
            };

            var policy = new TranslationQualityPolicy(0, 0.20, true);
            var summary = TranslationQualityGate.Evaluate(cases, reviews, policy);

            Assert.False(summary.Passed);
            Assert.Equal(0.25, summary.MajorOrWorseRate, 6);
        }

        [Fact]
        public void DuplicateReviewForSameCaseIsRejected()
        {
            var cases = new[]
            {
                Case("a", "Hello.", TranslationQualityDimension.Adequacy),
            };
            var reviews = new[]
            {
                Pass("a", "こんにちは。"),
                Pass("a", "やあ。"),
            };

            Assert.Throws<ArgumentException>(() =>
                TranslationQualityGate.Evaluate(cases, reviews, TranslationQualityPolicy.CandidatePromotion));
        }

        [Fact]
        public void ReviewForUnknownCaseIsRejected()
        {
            var cases = new[]
            {
                Case("a", "Hello.", TranslationQualityDimension.Adequacy),
            };
            var reviews = new[]
            {
                Pass("missing", "こんにちは。"),
            };

            Assert.Throws<ArgumentException>(() =>
                TranslationQualityGate.Evaluate(cases, reviews, TranslationQualityPolicy.CandidatePromotion));
        }

        [Fact]
        public void FailureMustNameAtLeastOneDimension()
        {
            Assert.Throws<ArgumentException>(() =>
                new TranslationQualityReview(
                    "a",
                    "bad",
                    TranslationQualitySeverity.Major,
                    Array.Empty<TranslationQualityDimension>(),
                    "Unstructured failure."));
        }

        private static TranslationQualityCase Case(
            string id,
            string source,
            TranslationQualityDimension dimension)
        {
            return new TranslationQualityCase(
                id,
                source,
                new[] { dimension, TranslationQualityDimension.JapaneseReadability },
                "Regression fixture for " + dimension + ".");
        }

        private static TranslationQualityReview Pass(string caseId, string candidateText)
        {
            return new TranslationQualityReview(
                caseId,
                candidateText,
                TranslationQualitySeverity.None,
                Array.Empty<TranslationQualityDimension>(),
                string.Empty);
        }
    }
}
