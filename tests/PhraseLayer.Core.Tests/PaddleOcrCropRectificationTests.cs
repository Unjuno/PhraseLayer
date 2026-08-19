using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class PaddleOcrCropRectificationTests
    {
        [Fact]
        public void HorizontalQuadPreservesExpectedWarpDimensions()
        {
            var quad = ImageQuad.FromRect(10, 20, 120, 40);

            var plan = PaddleOcrCropRectification.CreatePlan(quad);

            Assert.Equal(120, plan.WarpWidth);
            Assert.Equal(40, plan.WarpHeight);
            Assert.False(plan.RotateCounterClockwise90);
            Assert.Equal(120, plan.OutputWidth);
            Assert.Equal(40, plan.OutputHeight);
        }

        [Fact]
        public void OpposingEdgeMaximumsMatchPaddleOcrSizingRule()
        {
            var quad = new ImageQuad(
                new ImagePoint(0, 0),
                new ImagePoint(100.9, 0),
                new ImagePoint(120.2, 51.8),
                new ImagePoint(0, 40.1));

            var plan = PaddleOcrCropRectification.CreatePlan(quad);

            // Bottom edge is longer than top; right edge is longer than left.
            Assert.Equal((int)Math.Max(
                Distance(quad.P0, quad.P1),
                Distance(quad.P2, quad.P3)), plan.WarpWidth);
            Assert.Equal((int)Math.Max(
                Distance(quad.P0, quad.P3),
                Distance(quad.P1, quad.P2)), plan.WarpHeight);
        }

        [Fact]
        public void RotationThresholdIsInclusiveAtOnePointFive()
        {
            var plan = PaddleOcrCropRectification.CreatePlan(ImageQuad.FromRect(0, 0, 40, 60));

            Assert.True(plan.RotateCounterClockwise90);
            Assert.Equal(60, plan.OutputWidth);
            Assert.Equal(40, plan.OutputHeight);
        }

        [Fact]
        public void RatioBelowThresholdDoesNotRotate()
        {
            var plan = PaddleOcrCropRectification.CreatePlan(ImageQuad.FromRect(0, 0, 40, 59));

            Assert.False(plan.RotateCounterClockwise90);
            Assert.Equal(40, plan.OutputWidth);
            Assert.Equal(59, plan.OutputHeight);
        }

        [Fact]
        public void WarpDestinationUsesWidthAndHeightNotMinusOne()
        {
            var plan = PaddleOcrCropRectification.CreatePlan(ImageQuad.FromRect(0, 0, 100, 20));
            var destination = plan.WarpDestination;

            AssertPoint(destination.P0, 0, 0);
            AssertPoint(destination.P1, 100, 0);
            AssertPoint(destination.P2, 100, 20);
            AssertPoint(destination.P3, 0, 20);
        }

        [Fact]
        public void SubPixelDegenerateQuadRejectedAfterPaddleIntegerTruncation()
        {
            var tooNarrow = ImageQuad.FromRect(0, 0, 0.9, 10);
            var tooShort = ImageQuad.FromRect(0, 0, 10, 0.9);

            Assert.Throws<ArgumentException>(() => PaddleOcrCropRectification.CreatePlan(tooNarrow));
            Assert.Throws<ArgumentException>(() => PaddleOcrCropRectification.CreatePlan(tooShort));
        }

        [Fact]
        public void NonFiniteCoordinatesAreRejected()
        {
            var quad = new ImageQuad(
                new ImagePoint(double.NaN, 0),
                new ImagePoint(10, 0),
                new ImagePoint(10, 10),
                new ImagePoint(0, 10));

            Assert.Throws<ArgumentException>(() => PaddleOcrCropRectification.CreatePlan(quad));
        }

        private static double Distance(ImagePoint a, ImagePoint b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static void AssertPoint(ImagePoint point, double x, double y)
        {
            Assert.Equal(x, point.X);
            Assert.Equal(y, point.Y);
        }
    }
}
