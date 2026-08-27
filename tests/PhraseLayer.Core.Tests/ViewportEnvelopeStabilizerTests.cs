using System;
using PhraseLayer.Core.Spatial;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class ViewportEnvelopeStabilizerTests
    {
        [Fact]
        public void FirstObservationIsAcceptedWithoutLag()
        {
            var stabilizer = new ViewportEnvelopeStabilizer();
            var observed = new ViewportEnvelope(0.10, 0.20, 0.30, 0.40);

            var stabilized = stabilizer.Stabilize("unit-1", observed);

            Assert.Equal(observed.MinU, stabilized.MinU);
            Assert.Equal(observed.MinV, stabilized.MinV);
            Assert.Equal(observed.MaxU, stabilized.MaxU);
            Assert.Equal(observed.MaxV, stabilized.MaxV);
            Assert.Equal(1, stabilizer.Count);
        }

        [Fact]
        public void SmallOcrJitterIsExponentiallySmoothed()
        {
            var stabilizer = new ViewportEnvelopeStabilizer(new ViewportEnvelopeStabilizerOptions
            {
                BlendFactor = 0.25,
                ResetCenterDistance = 0.20,
            });

            stabilizer.Stabilize("unit-1", new ViewportEnvelope(0.10, 0.20, 0.30, 0.40));
            var stabilized = stabilizer.Stabilize("unit-1", new ViewportEnvelope(0.14, 0.24, 0.34, 0.44));

            Assert.Equal(0.11, stabilized.MinU, 6);
            Assert.Equal(0.21, stabilized.MinV, 6);
            Assert.Equal(0.31, stabilized.MaxU, 6);
            Assert.Equal(0.41, stabilized.MaxV, 6);
        }

        [Fact]
        public void LargeViewportMotionResetsImmediately()
        {
            var stabilizer = new ViewportEnvelopeStabilizer(new ViewportEnvelopeStabilizerOptions
            {
                BlendFactor = 0.10,
                ResetCenterDistance = 0.08,
            });

            stabilizer.Stabilize("unit-1", new ViewportEnvelope(0.10, 0.10, 0.20, 0.20));
            var moved = new ViewportEnvelope(0.70, 0.65, 0.80, 0.75);
            var stabilized = stabilizer.Stabilize("unit-1", moved);

            Assert.Equal(moved.MinU, stabilized.MinU);
            Assert.Equal(moved.MinV, stabilized.MinV);
            Assert.Equal(moved.MaxU, stabilized.MaxU);
            Assert.Equal(moved.MaxV, stabilized.MaxV);
        }

        [Fact]
        public void TargetsHaveIndependentStateAndResetClearsEncounterGeometry()
        {
            var stabilizer = new ViewportEnvelopeStabilizer();
            stabilizer.Stabilize("left", new ViewportEnvelope(0.10, 0.10, 0.20, 0.20));
            stabilizer.Stabilize("right", new ViewportEnvelope(0.70, 0.70, 0.80, 0.80));

            Assert.True(stabilizer.TryGet("left", out var left));
            Assert.True(stabilizer.TryGet("right", out var right));
            Assert.True(left.Center.U < right.Center.U);
            Assert.Equal(2, stabilizer.Count);

            stabilizer.Reset();

            Assert.Equal(0, stabilizer.Count);
            Assert.False(stabilizer.TryGet("left", out _));
            Assert.False(stabilizer.TryGet("right", out _));
        }

        [Fact]
        public void InvalidOptionsFailClosed()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ViewportEnvelopeStabilizer(
                new ViewportEnvelopeStabilizerOptions { BlendFactor = 0.0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ViewportEnvelopeStabilizer(
                new ViewportEnvelopeStabilizerOptions { ResetCenterDistance = -0.01 }));
        }
    }
}
