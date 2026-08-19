using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class PaddleOcrRecognitionTests
    {
        [Fact]
        public void DefaultRecognitionShapePreservesAspectRatioAndPadsRight()
        {
            var transform = PaddleOcrV6TinyRecognitionPreprocess.CreateResizeTransform(160, 48);

            Assert.Equal(320, transform.ModelWidth);
            Assert.Equal(48, transform.ModelHeight);
            Assert.Equal(160, transform.ResizedWidth);
            Assert.Equal(160, transform.RightPaddingWidth);
            AssertClose(1.0, transform.RatioWidth);
            AssertClose(1.0, transform.RatioHeight);
            AssertClose(0.5, transform.ValidRatio);
            Assert.False(transform.IsWidthCapped);
        }

        [Fact]
        public void WideCropIsCappedAtModelWidth()
        {
            var transform = PaddleOcrV6TinyRecognitionPreprocess.CreateResizeTransform(400, 48);

            Assert.Equal(320, transform.ResizedWidth);
            Assert.Equal(0, transform.RightPaddingWidth);
            AssertClose(0.8, transform.RatioWidth);
            AssertClose(1.0, transform.RatioHeight);
            AssertClose(1.0, transform.ValidRatio);
            Assert.True(transform.IsWidthCapped);
        }

        [Fact]
        public void DynamicWidthCanBeSelectedExplicitlyAfterModelProbe()
        {
            var transform = PaddleOcrV6TinyRecognitionPreprocess.CreateResizeTransform(800, 48, 960, 48);

            Assert.Equal(800, transform.ResizedWidth);
            Assert.Equal(160, transform.RightPaddingWidth);
            AssertClose(800.0 / 960.0, transform.ValidRatio);
            Assert.False(transform.IsWidthCapped);
        }

        [Fact]
        public void RecognitionNormalizationMapsByteEndpointsToMinusOneAndOne()
        {
            AssertClose(-1.0, PaddleOcrV6TinyRecognitionPreprocess.NormalizeChannel(0), 1e-6);
            AssertClose(1.0, PaddleOcrV6TinyRecognitionPreprocess.NormalizeChannel(255), 1e-6);
            AssertClose((128.0 / 255.0 - 0.5) / 0.5,
                PaddleOcrV6TinyRecognitionPreprocess.NormalizeChannel(128), 1e-6);
        }

        [Fact]
        public void CtcDecodeRemovesConsecutiveDuplicatesBeforeBlankFiltering()
        {
            var dictionary = new[] { "a", "b", " " };
            var indices = new[] { 1, 1, 0, 1, 2, 2, 0, 3 };
            var scores = new[] { 0.90f, 0.80f, 0.99f, 0.70f, 0.60f, 0.50f, 0.98f, 0.40f };

            var result = PaddleCtcGreedyDecoder.DecodeFromIndices(indices, scores, dictionary);

            Assert.Equal("aab ", result.Text);
            Assert.Equal(4, result.EmittedTokenCount);
            AssertClose((0.90 + 0.70 + 0.60 + 0.40) / 4.0, result.Confidence, 1e-6);
        }

        [Fact]
        public void BlankSeparatesOtherwiseRepeatedCharacters()
        {
            var dictionary = new[] { "x" };
            var result = PaddleCtcGreedyDecoder.DecodeFromIndices(
                new[] { 1, 0, 1 },
                new[] { 0.9f, 0.8f, 0.7f },
                dictionary);

            Assert.Equal("xx", result.Text);
            Assert.Equal(2, result.EmittedTokenCount);
            AssertClose(0.8, result.Confidence, 1e-6);
        }

        [Fact]
        public void AllBlankSequenceReturnsEmptyTextAndZeroConfidence()
        {
            var result = PaddleCtcGreedyDecoder.DecodeFromIndices(
                new[] { 0, 0, 0 },
                new[] { 0.9f, 0.8f, 0.7f },
                new[] { "a" });

            Assert.Equal(string.Empty, result.Text);
            Assert.Equal(0, result.EmittedTokenCount);
            AssertClose(0.0, result.Confidence);
        }

        [Fact]
        public void PredictionDecodeUsesFirstArgmaxOnTiesAndBlankClassZero()
        {
            var dictionary = new[] { "a", "b" };
            var predictions = new[]
            {
                0.1f, 0.7f, 0.7f,
                0.8f, 0.1f, 0.1f,
                0.1f, 0.2f, 0.9f,
            };

            var result = PaddleCtcGreedyDecoder.DecodeFromPredictions(
                predictions,
                timeSteps: 3,
                classCount: 3,
                characterDictionary: dictionary);

            Assert.Equal("ab", result.Text);
            Assert.Equal(2, result.EmittedTokenCount);
            AssertClose(0.8, result.Confidence, 1e-6);
        }

        [Fact]
        public void MultiCodepointDictionaryTokensAreJoinedWithoutAssumingSingleChars()
        {
            var dictionary = new[] { "th", "e" };
            var result = PaddleCtcGreedyDecoder.DecodeFromIndices(
                new[] { 1, 0, 2 },
                new[] { 0.9f, 0.8f, 0.7f },
                dictionary);

            Assert.Equal("the", result.Text);
        }

        [Fact]
        public void InvalidRecognitionInputsAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PaddleOcrV6TinyRecognitionPreprocess.CreateResizeTransform(0, 48));
            Assert.Throws<ArgumentException>(() =>
                PaddleCtcGreedyDecoder.DecodeFromPredictions(
                    new float[6], 2, 3, new[] { "a" }));
            Assert.Throws<ArgumentException>(() =>
                PaddleCtcGreedyDecoder.DecodeFromPredictions(
                    new float[5], 2, 2, new[] { "a" }));
            Assert.Throws<ArgumentException>(() =>
                PaddleCtcGreedyDecoder.DecodeFromIndices(
                    new[] { 1, 0 }, new[] { 0.9f }, new[] { "a" }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PaddleCtcGreedyDecoder.DecodeFromIndices(
                    new[] { 2 }, new[] { 0.9f }, new[] { "a" }));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PaddleCtcGreedyDecoder.DecodeFromIndices(
                    new[] { 1 }, new[] { float.NaN }, new[] { "a" }));
        }

        private static void AssertClose(double expected, double actual, double tolerance = 1e-9)
        {
            Assert.InRange(actual, expected - tolerance, expected + tolerance);
        }
    }
}
