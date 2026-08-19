using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class PaddleDbPostprocessTests
    {
        [Fact]
        public void V6TinySpecMatchesPinnedDetectorConfiguration()
        {
            var spec = PaddleDbPostprocessSpec.V6Tiny();

            AssertClose(0.2, spec.BitmapThreshold);
            AssertClose(0.4, spec.BoxThreshold);
            Assert.Equal(3000, spec.MaxCandidates);
            AssertClose(1.4, spec.UnclipRatio);
            Assert.Equal(PaddleDbScoreMode.Fast, spec.ScoreMode);
            AssertClose(3.0, spec.MinimumShortSide);
            AssertClose(5.0, spec.MinimumShortSideAfterUnclip);
        }

        [Fact]
        public void BitmapThresholdIsStrictButBoxThresholdAcceptsEquality()
        {
            var spec = PaddleDbPostprocessSpec.V6Tiny();

            Assert.False(spec.IsForeground(0.2));
            Assert.True(spec.IsForeground(0.2000001));
            Assert.False(spec.AcceptBoxScore(0.3999999));
            Assert.True(spec.AcceptBoxScore(0.4));
        }

        [Fact]
        public void ShortSideRequirementIncreasesByTwoAfterUnclip()
        {
            var spec = PaddleDbPostprocessSpec.V6Tiny();

            Assert.False(spec.AcceptShortSide(2.999, afterUnclip: false));
            Assert.True(spec.AcceptShortSide(3.0, afterUnclip: false));
            Assert.False(spec.AcceptShortSide(4.999, afterUnclip: true));
            Assert.True(spec.AcceptShortSide(5.0, afterUnclip: true));
        }

        [Fact]
        public void UnclipDistanceUsesAreaRatioOverPerimeter()
        {
            var spec = PaddleDbPostprocessSpec.V6Tiny();

            var distance = spec.ComputeUnclipDistance(200.0, 60.0);

            AssertClose(200.0 * 1.4 / 60.0, distance);
        }

        [Fact]
        public void BitmapPointScalingUsesTiesToEvenAndClipsToDestination()
        {
            var halfPixel = PaddleDbPostprocessSpec.ScaleBitmapPoint(
                new DbBitmapPoint(0.5, 1.5),
                bitmapWidth: 4,
                bitmapHeight: 4,
                destinationWidth: 4,
                destinationHeight: 4);
            var clipped = PaddleDbPostprocessSpec.ScaleBitmapPoint(
                new DbBitmapPoint(-10.0, 100.0),
                bitmapWidth: 40,
                bitmapHeight: 40,
                destinationWidth: 400,
                destinationHeight: 200);

            AssertClose(0.0, halfPixel.X);
            AssertClose(2.0, halfPixel.Y);
            AssertClose(0.0, clipped.X);
            AssertClose(200.0, clipped.Y);
        }

        [Fact]
        public void InvalidConfigurationAndMeasurementsAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new PaddleDbPostprocessSpec(-0.1, 0.4, 3000, 1.4));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new PaddleDbPostprocessSpec(0.2, 0.4, 0, 1.4));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new PaddleDbPostprocessSpec(0.2, 0.4, 3000, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new PaddleDbPostprocessSpec(0.2, 0.4, 3000, 1.4, (PaddleDbScoreMode)99));

            var spec = PaddleDbPostprocessSpec.V6Tiny();
            Assert.Throws<ArgumentOutOfRangeException>(() => spec.IsForeground(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => spec.AcceptBoxScore(1.1));
            Assert.Throws<ArgumentOutOfRangeException>(() => spec.AcceptShortSide(-1.0, false));
            Assert.Throws<ArgumentOutOfRangeException>(() => spec.ComputeUnclipDistance(1.0, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PaddleDbPostprocessSpec.ScaleBitmapPoint(default(DbBitmapPoint), 0, 1, 1, 1));
        }

        private static void AssertClose(double expected, double actual, double tolerance = 1e-9)
        {
            Assert.InRange(actual, expected - tolerance, expected + tolerance);
        }
    }
}
