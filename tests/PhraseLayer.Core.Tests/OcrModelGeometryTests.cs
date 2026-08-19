using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class OcrModelGeometryTests
    {
        [Fact]
        public void LandscapeSourceLetterboxesVertically()
        {
            var transform = OcrLetterboxTransform.Create(1280, 960, 640, 640);

            AssertClose(0.5, transform.Scale);
            AssertClose(0.0, transform.PaddingX);
            AssertClose(80.0, transform.PaddingY);
            AssertClose(640.0, transform.ResizedWidth);
            AssertClose(480.0, transform.ResizedHeight);

            var topLeft = transform.SourceToModel(new ImagePoint(0.0, 0.0));
            var bottomRight = transform.SourceToModel(new ImagePoint(1280.0, 960.0));

            AssertClose(0.0, topLeft.X);
            AssertClose(80.0, topLeft.Y);
            AssertClose(640.0, bottomRight.X);
            AssertClose(560.0, bottomRight.Y);
        }

        [Fact]
        public void PortraitSourceLetterboxesHorizontally()
        {
            var transform = OcrLetterboxTransform.Create(960, 1280, 640, 640);

            AssertClose(0.5, transform.Scale);
            AssertClose(80.0, transform.PaddingX);
            AssertClose(0.0, transform.PaddingY);
        }

        [Fact]
        public void QuadRoundTripsBetweenSourceAndModelSpace()
        {
            var transform = OcrLetterboxTransform.Create(1280, 960, 640, 640);
            var source = ImageQuad.FromRect(240.0, 150.0, 500.0, 120.0);

            var model = transform.SourceToModel(source);
            var roundTrip = transform.ModelToSource(model);

            AssertPoint(source.P0, roundTrip.P0);
            AssertPoint(source.P1, roundTrip.P1);
            AssertPoint(source.P2, roundTrip.P2);
            AssertPoint(source.P3, roundTrip.P3);
        }

        [Fact]
        public void NormalizedModelPointMapsThroughPaddingBeforeReturningToSource()
        {
            var transform = OcrLetterboxTransform.Create(1280, 960, 640, 640);

            var source = transform.NormalizedModelToSource(0.5, 0.5);

            AssertClose(640.0, source.X);
            AssertClose(480.0, source.Y);
        }

        [Fact]
        public void OutputMapperReturnsSourceGeometryAndConservativeConfidence()
        {
            var transform = OcrLetterboxTransform.Create(1280, 960, 640, 640);
            var sourceQuad = ImageQuad.FromRect(100.0, 200.0, 300.0, 80.0);
            var detection = new OcrDetectionCandidate(transform.SourceToModel(sourceQuad), 0.92);
            var recognition = new OcrRecognitionCandidate("keep off", 0.81);

            var region = OcrModelOutputMapper.ToSourceRegion(detection, recognition, transform);

            Assert.Equal("keep off", region.Text);
            AssertClose(0.81, region.Confidence);
            AssertPoint(sourceQuad.P0, region.ImageBounds.P0);
            AssertPoint(sourceQuad.P2, region.ImageBounds.P2);
        }

        [Fact]
        public void InvalidDimensionsAndConfidenceAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => OcrLetterboxTransform.Create(0, 960, 640, 640));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OcrRecognitionCandidate("text", double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OcrDetectionCandidate(default(OcrModelQuad), 1.1));
        }

        private static void AssertPoint(ImagePoint expected, ImagePoint actual)
        {
            AssertClose(expected.X, actual.X);
            AssertClose(expected.Y, actual.Y);
        }

        private static void AssertClose(double expected, double actual)
        {
            Assert.InRange(actual, expected - 1e-9, expected + 1e-9);
        }
    }
}
