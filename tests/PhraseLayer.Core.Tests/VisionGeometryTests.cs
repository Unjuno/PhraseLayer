using System.Collections.Generic;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class VisionGeometryTests
    {
        [Fact]
        public void ImagePointConversionFlipsTopLeftYIntoBottomLeftViewportY()
        {
            var point = ImageCoordinateMapper.ToViewport(new ImagePoint(200, 50), 1000, 500);
            Assert.Equal(0.20, point.U, 6);
            Assert.Equal(0.90, point.V, 6);
        }

        [Fact]
        public void DetectorOvershootIsClampedToViewportBounds()
        {
            var topLeft = ImageCoordinateMapper.ToViewport(new ImagePoint(-20, -10), 1000, 500);
            var bottomRight = ImageCoordinateMapper.ToViewport(new ImagePoint(1100, 700), 1000, 500);
            Assert.Equal(0.0, topLeft.U, 6);
            Assert.Equal(1.0, topLeft.V, 6);
            Assert.Equal(1.0, bottomRight.U, 6);
            Assert.Equal(0.0, bottomRight.V, 6);
        }

        [Fact]
        public void RectQuadMapsToExpectedViewportCenter()
        {
            var imageQuad = ImageQuad.FromRect(100, 50, 200, 100);
            var viewport = ImageCoordinateMapper.ToViewport(imageQuad, 1000, 500);
            Assert.Equal(0.20, viewport.Centroid.U, 6);
            Assert.Equal(0.80, viewport.Centroid.V, 6);
        }

        [Fact]
        public void OcrRegionsPreserveRotatedQuadAndExposeViewportAnchor()
        {
            var region = new OcrRegion(
                "keep off",
                0.97,
                new ImageQuad(
                    new ImagePoint(100, 100),
                    new ImagePoint(300, 80),
                    new ImagePoint(310, 180),
                    new ImagePoint(110, 200)));
            var observation = new OcrObservation("keep off", 0.97, new[] { region });
            var frame = new ImageFrame(new byte[4], 1000, 500, 1234);

            var mapped = Assert.Single(OcrViewportMapper.Map(observation, frame));
            Assert.Same(region, mapped.Source);
            Assert.Equal(0.205, mapped.Anchor.U, 6);
            Assert.Equal(0.72, mapped.Anchor.V, 6);
        }

        [Fact]
        public void LegacyOcrObservationRemainsSpatiallyEmpty()
        {
            var observation = new OcrObservation("plain text", 1.0);
            Assert.Empty(observation.Regions);
        }
    }
}
