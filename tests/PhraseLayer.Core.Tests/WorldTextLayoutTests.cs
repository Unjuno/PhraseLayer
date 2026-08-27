using System;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Spatial;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class WorldTextLayoutTests
    {
        [Fact]
        public void FlatFourCornerSurfaceProducesMetricTextFrame()
        {
            var rays = new ViewportEncodedRayProvider();
            var surfaces = new ViewportPlaneRaycaster((u, v) => 0.0, new SpatialVector3(0, 0, -1));
            var projection = ProjectExact(rays, surfaces, new ViewportEnvelope(0.2, 0.3, 0.4, 0.5));

            var layout = new WorldTextLayoutPlanner(rays, surfaces).Fit(projection);

            var target = Assert.Single(layout.Targets);
            Assert.True(target.IsReady);
            Assert.Equal(WorldTextLayoutFailure.None, target.Failure);
            var surface = target.Surface!.Value;
            Assert.Equal(0.6, surface.Center.X, 6);
            Assert.Equal(0.4, surface.Center.Y, 6);
            Assert.Equal(0.0, surface.Center.Z, 6);
            Assert.Equal(0.4, surface.WidthMeters, 6);
            Assert.Equal(0.2, surface.HeightMeters, 6);
            Assert.Equal(0.0, surface.MaxPlanarityErrorMeters, 6);
            Assert.Equal(1.0, surface.Right.X, 6);
            Assert.Equal(1.0, surface.Up.Y, 6);
            Assert.Equal(1.0, surface.Normal.Z, 6);
            Assert.Equal(1, layout.ReadyCount);
        }

        [Fact]
        public void MissingCornerSurfaceRefusesInPlaceFrame()
        {
            var rays = new ViewportEncodedRayProvider();
            var surfaces = new ViewportPlaneRaycaster(
                (u, v) => 0.0,
                new SpatialVector3(0, 0, -1),
                failWhen: (u, v) => u > 0.39 && v > 0.49);
            var projection = ProjectExact(
                rays,
                new ViewportPlaneRaycaster((u, v) => 0.0, new SpatialVector3(0, 0, -1)),
                new ViewportEnvelope(0.2, 0.3, 0.4, 0.5));

            var target = Assert.Single(new WorldTextLayoutPlanner(rays, surfaces).Fit(projection).Targets);

            Assert.False(target.IsReady);
            Assert.Equal(WorldTextLayoutFailure.SurfaceNotFound, target.Failure);
            Assert.Null(target.Surface);
        }

        [Fact]
        public void ExcessivelyWarpedCornersAreRejectedAsNonPlanar()
        {
            var rays = new ViewportEncodedRayProvider();
            var centerSurface = new ViewportPlaneRaycaster((u, v) => 0.0, new SpatialVector3(0, 0, -1));
            var projection = ProjectExact(rays, centerSurface, new ViewportEnvelope(0.2, 0.3, 0.4, 0.5));
            var warped = new ViewportPlaneRaycaster(
                (u, v) => u > 0.39 && v > 0.49 ? 0.20 : 0.0,
                new SpatialVector3(0, 0, -1));

            var target = Assert.Single(new WorldTextLayoutPlanner(
                rays,
                warped,
                maximumPlanarityErrorMeters: 0.03).Fit(projection).Targets);

            Assert.False(target.IsReady);
            Assert.Equal(WorldTextLayoutFailure.NonPlanarSurface, target.Failure);
        }

        [Fact]
        public void DivergentSurfaceNormalsAreRejected()
        {
            var rays = new ViewportEncodedRayProvider();
            var centerSurface = new ViewportPlaneRaycaster((u, v) => 0.0, new SpatialVector3(0, 0, -1));
            var projection = ProjectExact(rays, centerSurface, new ViewportEnvelope(0.2, 0.3, 0.4, 0.5));
            var divergent = new ViewportPlaneRaycaster(
                (u, v) => 0.0,
                new SpatialVector3(0, 0, -1),
                normalFor: (u, v) => u > 0.39 && v > 0.49
                    ? new SpatialVector3(1, 0, 0)
                    : new SpatialVector3(0, 0, -1));

            var target = Assert.Single(new WorldTextLayoutPlanner(
                rays,
                divergent,
                minimumNormalDot: 0.90).Fit(projection).Targets);

            Assert.False(target.IsReady);
            Assert.Equal(WorldTextLayoutFailure.InconsistentSurfaceNormals, target.Failure);
        }

        [Fact]
        public void AdjacentLabelIsNotPromotedToInPlaceWorldText()
        {
            var rays = new ViewportEncodedRayProvider();
            var surfaces = new ViewportPlaneRaycaster((u, v) => 0.0, new SpatialVector3(0, 0, -1));
            var target = BuildSpatialTarget(
                SpatialAssistanceCoverage.Partial,
                new ViewportEnvelope(0.2, 0.3, 0.4, 0.5));
            var projection = new SpatialProjectionPlanner(rays, surfaces)
                .Project(new SpatialAssistancePlan(new[] { target }));
            var rayCallsBeforeFit = rays.CallCount;
            var surfaceCallsBeforeFit = surfaces.CallCount;

            var fitted = Assert.Single(new WorldTextLayoutPlanner(rays, surfaces).Fit(projection).Targets);

            Assert.Equal(WorldTextLayoutFailure.NotInPlaceReplacement, fitted.Failure);
            Assert.False(fitted.IsReady);
            Assert.Equal(rayCallsBeforeFit, rays.CallCount);
            Assert.Equal(surfaceCallsBeforeFit, surfaces.CallCount);
        }

        private static SpatialProjectionPlan ProjectExact(
            IViewportRayProvider rays,
            ISurfaceRaycaster surfaces,
            ViewportEnvelope envelope)
        {
            var target = BuildSpatialTarget(SpatialAssistanceCoverage.Exact, envelope);
            return new SpatialProjectionPlanner(rays, surfaces)
                .Project(new SpatialAssistancePlan(new[] { target }));
        }

        private static SpatialAssistanceTarget BuildSpatialTarget(
            SpatialAssistanceCoverage coverage,
            ViewportEnvelope? envelope)
        {
            var unit = new SemanticUnit(
                "mwe:0:8",
                SemanticUnitKind.MultiwordExpression,
                0,
                8,
                "keep off",
                2);
            var segment = new MixedLanguageSegment("keep off", "立ち入らない", true, unit);
            return new SpatialAssistanceTarget(
                segment,
                Array.Empty<OcrTextRegionSpan>(),
                coverage,
                envelope);
        }

        private sealed class ViewportEncodedRayProvider : IViewportRayProvider
        {
            public int CallCount { get; private set; }

            public bool TryCreateRay(ViewportPoint point, out SpatialRay ray)
            {
                CallCount++;
                ray = new SpatialRay(
                    new SpatialVector3(point.U, point.V, -1.0),
                    new SpatialVector3(0, 0, 1));
                return true;
            }
        }

        private sealed class ViewportPlaneRaycaster : ISurfaceRaycaster
        {
            private readonly Func<double, double, double> zFor;
            private readonly SpatialVector3 defaultNormal;
            private readonly Func<double, double, bool>? failWhen;
            private readonly Func<double, double, SpatialVector3>? normalFor;

            public ViewportPlaneRaycaster(
                Func<double, double, double> zFor,
                SpatialVector3 defaultNormal,
                Func<double, double, bool>? failWhen = null,
                Func<double, double, SpatialVector3>? normalFor = null)
            {
                this.zFor = zFor;
                this.defaultNormal = defaultNormal;
                this.failWhen = failWhen;
                this.normalFor = normalFor;
            }

            public int CallCount { get; private set; }

            public bool TryRaycast(SpatialRay ray, out SurfaceHit hit)
            {
                CallCount++;
                var u = ray.Origin.X;
                var v = ray.Origin.Y;
                if (failWhen != null && failWhen(u, v))
                {
                    hit = default(SurfaceHit);
                    return false;
                }

                var z = zFor(u, v);
                hit = new SurfaceHit(
                    new SpatialVector3(u * 2.0, v, z),
                    normalFor != null ? normalFor(u, v) : defaultNormal,
                    1.0 + z);
                return true;
            }
        }
    }
}
