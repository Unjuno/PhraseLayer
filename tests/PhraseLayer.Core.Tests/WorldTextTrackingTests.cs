using System;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Spatial;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class WorldTextTrackingTests
    {
        [Fact]
        public void NearbySamePhraseKeepsIdentityAndSmoothsPosition()
        {
            var stabilizer = new WorldTextTrackStabilizer(
                maximumAssociationDistanceMeters: 0.20,
                retentionSeconds: 0.60,
                smoothingTimeConstantSeconds: 0.10);

            var first = stabilizer.Update(Layout(Target("keep off", "立ち入らない", 0.0)), 0);
            var firstTrack = Assert.Single(first.Tracks);
            var second = stabilizer.Update(Layout(Target("keep off", "立ち入らない", 0.10)), 100_000);
            var secondTrack = Assert.Single(second.Tracks);

            Assert.Equal(firstTrack.TrackId, secondTrack.TrackId);
            Assert.Equal(2, secondTrack.ObservationCount);
            Assert.True(secondTrack.ObservedThisFrame);
            Assert.True(secondTrack.Surface.Center.X > 0.0);
            Assert.True(secondTrack.Surface.Center.X < 0.10);
            Assert.Equal(1, second.ObservedCount);
            Assert.Equal(0, second.RetainedCount);
        }

        [Fact]
        public void SamePhraseFarAwayCreatesSeparateTrack()
        {
            var stabilizer = new WorldTextTrackStabilizer(maximumAssociationDistanceMeters: 0.10);

            var first = stabilizer.Update(Layout(Target("exit", "出口", 0.0)), 0);
            var second = stabilizer.Update(Layout(Target("exit", "出口", 0.5)), 100_000);

            Assert.Single(first.Tracks);
            Assert.Equal(2, second.Tracks.Count);
            Assert.Equal(1, second.ObservedCount);
            Assert.Equal(1, second.RetainedCount);
            Assert.NotEqual(second.Tracks[0].TrackId, second.Tracks[1].TrackId);
        }

        [Fact]
        public void ShortObservationGapRetainsTrackThenExpiresIt()
        {
            var stabilizer = new WorldTextTrackStabilizer(retentionSeconds: 0.60);
            var first = stabilizer.Update(Layout(Target("warning", "警告", 0.0)), 0);
            var id = Assert.Single(first.Tracks).TrackId;

            var retained = stabilizer.Update(new WorldTextLayoutPlan(Array.Empty<WorldTextLayoutTarget>()), 400_000);
            var retainedTrack = Assert.Single(retained.Tracks);
            Assert.Equal(id, retainedTrack.TrackId);
            Assert.False(retainedTrack.ObservedThisFrame);
            Assert.Equal(1, retained.RetainedCount);

            var expired = stabilizer.Update(new WorldTextLayoutPlan(Array.Empty<WorldTextLayoutTarget>()), 600_001);
            Assert.Empty(expired.Tracks);
            Assert.Equal(0, stabilizer.ActiveTrackCount);
        }

        [Fact]
        public void ReappearanceAfterRetentionWindowGetsNewIdentity()
        {
            var stabilizer = new WorldTextTrackStabilizer(retentionSeconds: 0.20);
            var firstId = Assert.Single(stabilizer.Update(Layout(Target("door", "ドア", 0.0)), 0).Tracks).TrackId;

            stabilizer.Update(new WorldTextLayoutPlan(Array.Empty<WorldTextLayoutTarget>()), 250_000);
            var secondId = Assert.Single(stabilizer.Update(Layout(Target("door", "ドア", 0.01)), 300_000).Tracks).TrackId;

            Assert.NotEqual(firstId, secondId);
        }

        [Fact]
        public void ChangedTranslationDoesNotReuseOldDisplayTrack()
        {
            var stabilizer = new WorldTextTrackStabilizer(maximumAssociationDistanceMeters: 0.20);
            var first = stabilizer.Update(Layout(Target("keep off", "立ち入らない", 0.0)), 0);
            var firstId = Assert.Single(first.Tracks).TrackId;

            var second = stabilizer.Update(Layout(Target("keep off", "進入禁止", 0.01)), 100_000);

            Assert.Equal(2, second.Tracks.Count);
            Assert.Contains(second.Tracks, track => track.TrackId == firstId && !track.ObservedThisFrame);
            Assert.Contains(second.Tracks, track => track.TrackId != firstId && track.ObservedThisFrame);
        }

        [Fact]
        public void AxisSignNoiseDoesNotFlipTrackedOrientation()
        {
            var stabilizer = new WorldTextTrackStabilizer(smoothingTimeConstantSeconds: 0.10);
            stabilizer.Update(Layout(Target("text", "文字", 0.0)), 0);

            var flipped = Target(
                "text",
                "文字",
                0.01,
                right: new SpatialVector3(-1, 0, 0),
                up: new SpatialVector3(0, -1, 0),
                normal: new SpatialVector3(0, 0, 1));
            var track = Assert.Single(stabilizer.Update(Layout(flipped), 100_000).Tracks);

            Assert.True(track.Surface.Right.X > 0.99);
            Assert.True(track.Surface.Up.Y > 0.99);
            Assert.True(track.Surface.Normal.Z > 0.99);
        }

        [Fact]
        public void BackwardTimestampIsRejected()
        {
            var stabilizer = new WorldTextTrackStabilizer();
            stabilizer.Update(Layout(Target("exit", "出口", 0.0)), 1_000_000);

            Assert.Throws<ArgumentException>(() =>
                stabilizer.Update(Layout(Target("exit", "出口", 0.0)), 999_999));
        }

        private static WorldTextLayoutPlan Layout(params WorldTextLayoutTarget[] targets)
        {
            return new WorldTextLayoutPlan(targets);
        }

        private static WorldTextLayoutTarget Target(
            string sourceText,
            string displayText,
            double centerX,
            SpatialVector3? right = null,
            SpatialVector3? up = null,
            SpatialVector3? normal = null)
        {
            var unit = new SemanticUnit(
                "mwe:0:" + sourceText.Length,
                SemanticUnitKind.MultiwordExpression,
                0,
                sourceText.Length,
                sourceText,
                1);
            var segment = new MixedLanguageSegment(sourceText, displayText, true, unit);
            var spatial = new SpatialAssistanceTarget(
                segment,
                Array.Empty<OcrTextRegionSpan>(),
                SpatialAssistanceCoverage.Exact,
                new ViewportEnvelope(0.2, 0.3, 0.4, 0.5));
            var ray = new SpatialRay(
                new SpatialVector3(0, 0, -1),
                new SpatialVector3(0, 0, 1));
            var hit = new SurfaceHit(
                new SpatialVector3(centerX, 0, 0),
                new SpatialVector3(0, 0, -1),
                1.0);
            var projected = new ProjectedAssistanceTarget(
                spatial,
                OverlayPlacementKind.InPlaceReplacement,
                SpatialProjectionFailure.None,
                new ViewportPoint(0.3, 0.4),
                ray,
                hit);
            var surface = new WorldTextSurface(
                new SpatialVector3(centerX, 0, 0),
                right ?? new SpatialVector3(1, 0, 0),
                up ?? new SpatialVector3(0, 1, 0),
                normal ?? new SpatialVector3(0, 0, 1),
                0.20,
                0.05,
                0.0);
            return new WorldTextLayoutTarget(projected, WorldTextLayoutFailure.None, surface);
        }
    }
}
