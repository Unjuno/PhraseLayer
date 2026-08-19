using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class PaddleDbQuadPostprocessorTests
    {
        [Fact]
        public void TensorFactoryAcceptsCommonSingleMapLayouts()
        {
            var values = new float[12];

            AssertMap(PaddleDbProbabilityMap.FromTensor(new[] { 1, 1, 3, 4 }, values), 4, 3);
            AssertMap(PaddleDbProbabilityMap.FromTensor(new[] { 1, 3, 4 }, values), 4, 3);
            AssertMap(PaddleDbProbabilityMap.FromTensor(new[] { 3, 4 }, values), 4, 3);
        }

        [Fact]
        public void TensorFactoryRejectsAmbiguousOrMismatchedLayouts()
        {
            Assert.Throws<ArgumentException>(() =>
                PaddleDbProbabilityMap.FromTensor(new[] { 2, 1, 3, 4 }, new float[24]));
            Assert.Throws<ArgumentException>(() =>
                PaddleDbProbabilityMap.FromTensor(new[] { 1, 2, 3, 4 }, new float[24]));
            Assert.Throws<ArgumentException>(() =>
                PaddleDbProbabilityMap.FromTensor(new[] { 1, 1, 3, 4 }, new float[11]));
            Assert.Throws<ArgumentException>(() =>
                PaddleDbProbabilityMap.FromTensor(new[] { 1, 1, 1, 3, 4 }, new float[12]));
        }

        [Fact]
        public void RectangularBlobProducesScoredExpandedQuad()
        {
            const int width = 20;
            const int height = 20;
            var values = Filled(width * height, 0.1f);
            FillRect(values, width, x0: 5, y0: 5, x1Inclusive: 14, y1Inclusive: 10, value: 0.9f);

            var processor = new PaddleDbQuadPostprocessor(PaddleDbPostprocessSpec.V6Tiny());
            var detections = processor.Process(
                new PaddleDbProbabilityMap(values, width, height),
                destinationWidth: width,
                destinationHeight: height);

            var detection = Assert.Single(detections);
            Assert.InRange(detection.Score, 0.89999, 0.90001);
            AssertPoint(detection.ImageBounds.P0, 3, 3);
            AssertPoint(detection.ImageBounds.P1, 16, 3);
            AssertPoint(detection.ImageBounds.P2, 16, 12);
            AssertPoint(detection.ImageBounds.P3, 3, 12);
        }

        [Fact]
        public void BitmapThresholdRemainsStrict()
        {
            const int width = 12;
            const int height = 12;
            var values = Filled(width * height, 0.2f);

            var processor = new PaddleDbQuadPostprocessor(PaddleDbPostprocessSpec.V6Tiny());
            var detections = processor.Process(
                new PaddleDbProbabilityMap(values, width, height), width, height);

            Assert.Empty(detections);
        }

        [Fact]
        public void BoxThresholdRejectsForegroundWithInsufficientMeanScore()
        {
            const int width = 20;
            const int height = 20;
            var values = Filled(width * height, 0.1f);
            FillRect(values, width, 5, 5, 14, 10, 0.5f);
            var strict = new PaddleDbPostprocessSpec(
                bitmapThreshold: 0.2,
                boxThreshold: 0.8,
                maxCandidates: 3000,
                unclipRatio: 1.4,
                scoreMode: PaddleDbScoreMode.Fast,
                minimumShortSide: 3.0);

            var detections = new PaddleDbQuadPostprocessor(strict).Process(
                new PaddleDbProbabilityMap(values, width, height), width, height);

            Assert.Empty(detections);
        }

        [Fact]
        public void TinyComponentIsRejectedBeforeScoring()
        {
            const int width = 10;
            const int height = 10;
            var values = Filled(width * height, 0.1f);
            FillRect(values, width, 2, 2, 4, 4, 0.95f);

            var detections = new PaddleDbQuadPostprocessor(PaddleDbPostprocessSpec.V6Tiny()).Process(
                new PaddleDbProbabilityMap(values, width, height), width, height);

            Assert.Empty(detections);
        }

        [Fact]
        public void DestinationScalingUsesOriginalPaddleRoundingContract()
        {
            const int width = 20;
            const int height = 20;
            var values = Filled(width * height, 0.1f);
            FillRect(values, width, 5, 5, 14, 10, 0.9f);

            var detection = Assert.Single(
                new PaddleDbQuadPostprocessor(PaddleDbPostprocessSpec.V6Tiny()).Process(
                    new PaddleDbProbabilityMap(values, width, height),
                    destinationWidth: 200,
                    destinationHeight: 100));

            AssertPoint(detection.ImageBounds.P0, 28, 14);
            AssertPoint(detection.ImageBounds.P1, 162, 14);
            AssertPoint(detection.ImageBounds.P2, 162, 61);
            AssertPoint(detection.ImageBounds.P3, 28, 61);
        }

        [Fact]
        public void SlowScoreModeIsRejectedExplicitly()
        {
            var spec = new PaddleDbPostprocessSpec(
                0.2, 0.4, 3000, 1.4, PaddleDbScoreMode.Slow, 3.0);

            Assert.Throws<NotSupportedException>(() => new PaddleDbQuadPostprocessor(spec));
        }

        [Fact]
        public void InvalidProbabilityValuesAreRejected()
        {
            var values = Filled(100, 0.1f);
            values[10] = float.NaN;
            var processor = new PaddleDbQuadPostprocessor(PaddleDbPostprocessSpec.V6Tiny());

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                processor.Process(new PaddleDbProbabilityMap(values, 10, 10), 10, 10));
        }

        private static float[] Filled(int length, float value)
        {
            var values = new float[length];
            for (var index = 0; index < values.Length; index++)
                values[index] = value;
            return values;
        }

        private static void FillRect(
            float[] values,
            int width,
            int x0,
            int y0,
            int x1Inclusive,
            int y1Inclusive,
            float value)
        {
            for (var y = y0; y <= y1Inclusive; y++)
                for (var x = x0; x <= x1Inclusive; x++)
                    values[(y * width) + x] = value;
        }

        private static void AssertMap(PaddleDbProbabilityMap map, int width, int height)
        {
            Assert.Equal(width, map.Width);
            Assert.Equal(height, map.Height);
            Assert.Equal(width * height, map.Values.Length);
        }

        private static void AssertPoint(ImagePoint point, double x, double y)
        {
            Assert.Equal(x, point.X);
            Assert.Equal(y, point.Y);
        }
    }
}
