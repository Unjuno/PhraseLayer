using System;
using PhraseLayer.Core.Spatial;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class SurfaceHitStabilizerTests
    {
        [Fact]
        public void FirstVerifiedHitIsAcceptedWithoutLag()
        {
            var stabilizer = new SurfaceHitStabilizer();
            var observed = Hit(0.10, 0.20, 1.00, 0.0, 0.0, -1.0, 1.03);

            var stabilized = stabilizer.Stabilize("unit-1", observed);

            Assert.Equal(observed.Point.X, stabilized.Point.X);
            Assert.Equal(observed.Point.Y, stabilized.Point.Y);
            Assert.Equal(observed.Point.Z, stabilized.Point.Z);
            Assert.Equal(observed.Normal.Z, stabilized.Normal.Z);
            Assert.Equal(observed.DistanceMeters, stabilized.DistanceMeters);
            Assert.Equal(1, stabilizer.Count);
        }

        [Fact]
        public void SmallPointAndNormalJitterAreSmoothed()
        {
            var stabilizer = new SurfaceHitStabilizer(new SurfaceHitStabilizerOptions
            {
                BlendFactor = 0.25,
                ResetPointDistanceMeters = 0.50,
                ResetNormalAngleDegrees = 45.0,
            });

            stabilizer.Stabilize("unit-1", Hit(0.00, 0.00, 1.00, 0.0, 0.0, -1.0, 1.00));
            var stabilized = stabilizer.Stabilize(
                "unit-1",
                Hit(0.04, 0.08, 1.12, 0.20, 0.0, -0.98, 1.20));

            Assert.Equal(0.01, stabilized.Point.X, 6);
            Assert.Equal(0.02, stabilized.Point.Y, 6);
            Assert.Equal(1.03, stabilized.Point.Z, 6);
            Assert.Equal(1.05, stabilized.DistanceMeters, 6);
            Assert.Equal(1.0, Math.Sqrt(stabilized.Normal.SquaredMagnitude), 6);
            Assert.True(stabilized.Normal.X > 0.0);
            Assert.True(stabilized.Normal.Z < 0.0);
        }

        [Fact]
        public void LargeWorldMotionResetsImmediately()
        {
            var stabilizer = new SurfaceHitStabilizer(new SurfaceHitStabilizerOptions
            {
                BlendFactor = 0.10,
                ResetPointDistanceMeters = 0.10,
                ResetNormalAngleDegrees = 45.0,
            });

            stabilizer.Stabilize("unit-1", Hit(0.00, 0.00, 1.00, 0.0, 0.0, -1.0, 1.00));
            var moved = Hit(0.40, 0.00, 1.00, 0.0, 0.0, -1.0, 1.08);
            var stabilized = stabilizer.Stabilize("unit-1", moved);

            Assert.Equal(moved.Point.X, stabilized.Point.X);
            Assert.Equal(moved.Point.Z, stabilized.Point.Z);
            Assert.Equal(moved.DistanceMeters, stabilized.DistanceMeters);
        }

        [Fact]
        public void LargeNormalChangeResetsImmediately()
        {
            var stabilizer = new SurfaceHitStabilizer(new SurfaceHitStabilizerOptions
            {
                BlendFactor = 0.10,
                ResetPointDistanceMeters = 1.0,
                ResetNormalAngleDegrees = 15.0,
            });

            stabilizer.Stabilize("unit-1", Hit(0.00, 0.00, 1.00, 0.0, 0.0, -1.0, 1.00));
            var changedSurface = Hit(0.01, 0.00, 1.00, 0.0, 1.0, 0.0, 1.00);
            var stabilized = stabilizer.Stabilize("unit-1", changedSurface);

            Assert.Equal(changedSurface.Normal.X, stabilized.Normal.X);
            Assert.Equal(changedSurface.Normal.Y, stabilized.Normal.Y);
            Assert.Equal(changedSurface.Normal.Z, stabilized.Normal.Z);
        }

        [Fact]
        public void RaycastMissIsHeldOnlyForConfiguredBudget()
        {
            var stabilizer = new SurfaceHitStabilizer(new SurfaceHitStabilizerOptions
            {
                MaxMissingObservations = 1,
            });
            var observed = Hit(0.00, 0.00, 1.00, 0.0, 0.0, -1.0, 1.00);
            stabilizer.Stabilize("unit-1", observed);

            Assert.True(stabilizer.TryHoldMissing("unit-1", out var held));
            Assert.False(stabilizer.TryHoldMissing("unit-1", out _));
            Assert.Equal(observed.Point.Z, held.Point.Z);
            Assert.Equal(0, stabilizer.Count);
        }

        [Fact]
        public void FreshVerifiedHitResetsMissBudget()
        {
            var stabilizer = new SurfaceHitStabilizer(new SurfaceHitStabilizerOptions
            {
                MaxMissingObservations = 1,
            });

            stabilizer.Stabilize("unit-1", Hit(0.00, 0.00, 1.00, 0.0, 0.0, -1.0, 1.00));
            Assert.True(stabilizer.TryHoldMissing("unit-1", out _));

            stabilizer.Stabilize("unit-1", Hit(0.01, 0.00, 1.00, 0.0, 0.0, -1.0, 1.00));

            Assert.True(stabilizer.TryHoldMissing("unit-1", out _));
        }

        [Fact]
        public void TargetsHaveIndependentStateAndResetClearsEncounterGeometry()
        {
            var stabilizer = new SurfaceHitStabilizer();
            stabilizer.Stabilize("left", Hit(-0.2, 0.0, 1.0, 0.0, 0.0, -1.0, 1.0));
            stabilizer.Stabilize("right", Hit(0.2, 0.0, 1.0, 0.0, 0.0, -1.0, 1.0));

            Assert.True(stabilizer.TryGet("left", out var left));
            Assert.True(stabilizer.TryGet("right", out var right));
            Assert.True(left.Point.X < right.Point.X);
            Assert.Equal(2, stabilizer.Count);

            stabilizer.Reset();

            Assert.Equal(0, stabilizer.Count);
            Assert.False(stabilizer.TryGet("left", out _));
            Assert.False(stabilizer.TryGet("right", out _));
        }

        [Fact]
        public void InvalidOptionsFailClosed()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SurfaceHitStabilizer(
                new SurfaceHitStabilizerOptions { BlendFactor = 0.0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SurfaceHitStabilizer(
                new SurfaceHitStabilizerOptions { ResetPointDistanceMeters = -0.01 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SurfaceHitStabilizer(
                new SurfaceHitStabilizerOptions { ResetNormalAngleDegrees = 181.0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SurfaceHitStabilizer(
                new SurfaceHitStabilizerOptions { MaxMissingObservations = -1 }));
        }

        private static SurfaceHit Hit(
            double x,
            double y,
            double z,
            double normalX,
            double normalY,
            double normalZ,
            double distanceMeters)
        {
            return new SurfaceHit(
                new SpatialVector3(x, y, z),
                new SpatialVector3(normalX, normalY, normalZ),
                distanceMeters);
        }
    }
}
