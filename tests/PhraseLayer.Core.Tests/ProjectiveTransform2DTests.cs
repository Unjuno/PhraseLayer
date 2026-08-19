using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class ProjectiveTransform2DTests
    {
        [Fact]
        public void UnitSquareCornersMapExactlyToQuadCorners()
        {
            var quad = new ImageQuad(
                new ImagePoint(10, 20),
                new ImagePoint(120, 10),
                new ImagePoint(110, 90),
                new ImagePoint(0, 70));

            var transform = ProjectiveTransformFactory.UnitSquareToQuad(quad);

            AssertPoint(transform.Map(0, 0), quad.P0);
            AssertPoint(transform.Map(1, 0), quad.P1);
            AssertPoint(transform.Map(1, 1), quad.P2);
            AssertPoint(transform.Map(0, 1), quad.P3);
        }

        [Fact]
        public void RectangleProducesAffineMidpointMapping()
        {
            var quad = ImageQuad.FromRect(10, 20, 100, 40);
            var transform = ProjectiveTransformFactory.UnitSquareToQuad(quad);

            AssertPoint(transform.Map(0.5, 0.5), new ImagePoint(60, 40));
            Assert.Equal(0.0, transform.M20, 12);
            Assert.Equal(0.0, transform.M21, 12);
        }

        [Fact]
        public void NonAffineQuadUsesProjectiveDenominatorAndStillMapsCorners()
        {
            var quad = new ImageQuad(
                new ImagePoint(0, 0),
                new ImagePoint(100, 0),
                new ImagePoint(80, 100),
                new ImagePoint(20, 100));

            var transform = ProjectiveTransformFactory.UnitSquareToQuad(quad);

            Assert.True(Math.Abs(transform.M20) > 1e-9 || Math.Abs(transform.M21) > 1e-9);
            AssertPoint(transform.Map(1, 1), quad.P2);
        }

        [Fact]
        public void SingularProjectiveQuadIsRejected()
        {
            var quad = new ImageQuad(
                new ImagePoint(0, 0),
                new ImagePoint(10, 0),
                new ImagePoint(20, 0),
                new ImagePoint(30, 0));

            Assert.Throws<ArgumentException>(() => ProjectiveTransformFactory.UnitSquareToQuad(quad));
        }

        private static void AssertPoint(ImagePoint actual, ImagePoint expected)
        {
            Assert.InRange(actual.X, expected.X - 1e-9, expected.X + 1e-9);
            Assert.InRange(actual.Y, expected.Y - 1e-9, expected.Y + 1e-9);
        }
    }
}
