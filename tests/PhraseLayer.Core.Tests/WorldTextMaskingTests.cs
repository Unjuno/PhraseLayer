using System;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Spatial;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class WorldTextMaskingTests
    {
        [Fact]
        public void FirstObservationIsNotEnoughToCoverPhysicalText()
        {
            var policy = new WorldTextMaskPolicy(minimumObservationCount: 2);
            var track = Track(observedThisFrame: true, observationCount: 1);

            var decision = policy.Evaluate(track);

            Assert.False(decision.CanMask);
            Assert.Equal(WorldTextMaskSuppressionReason.InsufficientObservations, decision.SuppressionReason);
        }

        [Fact]
        public void RepeatedCurrentObservationCanMaskReplacement()
        {
            var policy = new WorldTextMaskPolicy(minimumObservationCount: 2, maximumPlanarityErrorMeters: 0.01);
            var track = Track(observedThisFrame: true, observationCount: 2, planarityErrorMeters: 0.004);

            var decision = policy.Evaluate(track);

            Assert.True(decision.CanMask);
            Assert.Equal(WorldTextMaskSuppressionReason.None, decision.SuppressionReason);
        }

        [Fact]
        public void RetainedTrackNeverKeepsPhysicalMaskAlive()
        {
            var policy = new WorldTextMaskPolicy(minimumObservationCount: 1);
            var track = Track(observedThisFrame: false, observationCount: 8);

            var decision = policy.Evaluate(track);

            Assert.False(decision.CanMask);
            Assert.Equal(WorldTextMaskSuppressionReason.NotObservedThisFrame, decision.SuppressionReason);
        }

        [Fact]
        public void ExcessivePlanarityErrorSuppressesMaskEvenWhenLayoutTrackExists()
        {
            var policy = new WorldTextMaskPolicy(minimumObservationCount: 1, maximumPlanarityErrorMeters: 0.005);
            var track = Track(observedThisFrame: true, observationCount: 4, planarityErrorMeters: 0.006);

            var decision = policy.Evaluate(track);

            Assert.False(decision.CanMask);
            Assert.Equal(WorldTextMaskSuppressionReason.ExcessivePlanarityError, decision.SuppressionReason);
        }

        [Fact]
        public void UnchangedDisplayTextDoesNotCoverSource()
        {
            var policy = new WorldTextMaskPolicy(minimumObservationCount: 1);
            var track = Track(observedThisFrame: true, observationCount: 2, sourceText: "EXIT", displayText: "EXIT");

            var decision = policy.Evaluate(track);

            Assert.False(decision.CanMask);
            Assert.Equal(WorldTextMaskSuppressionReason.NoVisibleReplacement, decision.SuppressionReason);
        }

        [Fact]
        public void InvalidConfigurationIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new WorldTextMaskPolicy(minimumObservationCount: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WorldTextMaskPolicy(maximumPlanarityErrorMeters: -0.001));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WorldTextMaskPolicy(maximumPlanarityErrorMeters: double.NaN));
        }

        private static WorldTextTrackState Track(
            bool observedThisFrame,
            int observationCount,
            double planarityErrorMeters = 0.0,
            string sourceText = "keep off",
            string displayText = "立ち入らない")
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
                new SpatialVector3(0, 0, 0),
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
                new SpatialVector3(0, 0, 0),
                new SpatialVector3(1, 0, 0),
                new SpatialVector3(0, 1, 0),
                new SpatialVector3(0, 0, 1),
                0.20,
                0.05,
                planarityErrorMeters);
            var target = new WorldTextLayoutTarget(projected, WorldTextLayoutFailure.None, surface);
            return new WorldTextTrackState(
                1,
                target,
                surface,
                observedThisFrame,
                0,
                100_000,
                observationCount);
        }
    }
}
