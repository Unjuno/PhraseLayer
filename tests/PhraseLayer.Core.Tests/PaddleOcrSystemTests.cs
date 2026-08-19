using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class PaddleOcrSystemTests
    {
        [Fact]
        public void ReadingOrderMirrorsPaddleSameLineSwap()
        {
            var detections = new[]
            {
                Detection(x: 80, y: 20, textWidth: 20, textHeight: 10),
                Detection(x: 10, y: 25, textWidth: 20, textHeight: 10),
                Detection(x: 5, y: 50, textWidth: 20, textHeight: 10),
            };

            var sorted = PaddleOcrReadingOrder.Sort(detections);

            Assert.Equal(10, sorted[0].ImageBounds.P0.X);
            Assert.Equal(80, sorted[1].ImageBounds.P0.X);
            Assert.Equal(5, sorted[2].ImageBounds.P0.X);
        }

        [Fact]
        public void SameLineToleranceIsStrictlyLessThanTenPixels()
        {
            var detections = new[]
            {
                Detection(x: 80, y: 20, textWidth: 20, textHeight: 10),
                Detection(x: 10, y: 30, textWidth: 20, textHeight: 10),
            };

            var sorted = PaddleOcrReadingOrder.Sort(detections);

            Assert.Equal(80, sorted[0].ImageBounds.P0.X);
            Assert.Equal(10, sorted[1].ImageBounds.P0.X);
        }

        [Fact]
        public void ObservationAssemblerKeepsScoreEqualToDropThreshold()
        {
            var candidates = new[]
            {
                Candidate("keep", 0.5, x: 0, y: 0),
                Candidate("drop", 0.499, x: 0, y: 20),
            };

            var observation = PaddleOcrObservationAssembler.Assemble(candidates);

            Assert.Equal("keep", observation.Text);
            var region = Assert.Single(observation.Regions);
            Assert.Equal("keep", region.Text);
            Assert.Equal(0.5, observation.Confidence, 12);
        }

        [Fact]
        public void ObservationAssemblerPreservesCandidateOrderAndAveragesRecognitionConfidence()
        {
            var candidates = new[]
            {
                Candidate("first", 0.8, x: 0, y: 0),
                Candidate("second", 0.6, x: 0, y: 20),
            };

            var observation = PaddleOcrObservationAssembler.Assemble(candidates, 0.5, " ");

            Assert.Equal("first second", observation.Text);
            Assert.Equal(0.7, observation.Confidence, 12);
            Assert.Equal("first", observation.Regions[0].Text);
            Assert.Equal("second", observation.Regions[1].Text);
        }

        [Fact]
        public void DetectionScoreIsNotMultipliedIntoRecognitionConfidence()
        {
            var candidate = new PaddleOcrRecognizedCandidate(
                "text",
                recognitionConfidence: 0.9,
                detectionScore: 0.4,
                ImageQuad.FromRect(0, 0, 20, 10));

            var observation = PaddleOcrObservationAssembler.Assemble(new[] { candidate });

            Assert.Equal(0.9, observation.Confidence, 12);
            Assert.Equal(0.9, observation.Regions[0].Confidence, 12);
        }

        [Fact]
        public void NoAcceptedRegionsProducesEmptyZeroConfidenceObservation()
        {
            var observation = PaddleOcrObservationAssembler.Assemble(new[]
            {
                Candidate("low", 0.2, x: 0, y: 0),
            });

            Assert.Equal(string.Empty, observation.Text);
            Assert.Empty(observation.Regions);
            Assert.Equal(0.0, observation.Confidence, 12);
        }

        [Theory]
        [InlineData(-0.01)]
        [InlineData(1.01)]
        [InlineData(double.NaN)]
        public void CandidateRejectsRecognitionConfidenceOutsideUnitInterval(double confidence)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PaddleOcrRecognizedCandidate(
                "x", confidence, 0.5, ImageQuad.FromRect(0, 0, 10, 10)));
        }

        private static PaddleDbQuadDetection Detection(double x, double y, double textWidth, double textHeight)
        {
            return new PaddleDbQuadDetection(ImageQuad.FromRect(x, y, textWidth, textHeight), 0.9);
        }

        private static PaddleOcrRecognizedCandidate Candidate(string text, double confidence, double x, double y)
        {
            return new PaddleOcrRecognizedCandidate(
                text,
                confidence,
                detectionScore: 0.9,
                ImageQuad.FromRect(x, y, 20, 10));
        }
    }
}
