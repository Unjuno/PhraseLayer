using System;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Semantics;
using PhraseLayer.Core.Spatial;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class SurfaceNormalOrientationTests
    {
        [Fact]
        public void BackFacingSurfaceNormalIsFlippedTowardRayOrigin()
        {
            var planner = new SpatialProjectionPlanner(
                new ConstantRayProvider(new SpatialVector3(0.0, 0.0, 1.0)),
                new ConstantSurfaceRaycaster(new SpatialVector3(0.0, 0.0, 1.0)));

            var projected = Assert.Single(planner.Project(new SpatialAssistancePlan(new[] { BuildTarget() })).Targets);

            Assert.True(projected.Surface.HasValue);
            Assert.Equal(-1.0, projected.Surface.Value.Normal.Z, 6);
            Assert.Equal(2.0, projected.Surface.Value.DistanceMeters, 6);
        }

        [Fact]
        public void AlreadyCameraFacingSurfaceNormalIsPreserved()
        {
            var planner = new SpatialProjectionPlanner(
                new ConstantRayProvider(new SpatialVector3(0.0, 0.0, 1.0)),
                new ConstantSurfaceRaycaster(new SpatialVector3(0.0, 0.0, -1.0)));

            var projected = Assert.Single(planner.Project(new SpatialAssistancePlan(new[] { BuildTarget() })).Targets);

            Assert.True(projected.Surface.HasValue);
            Assert.Equal(-1.0, projected.Surface.Value.Normal.Z, 6);
        }

        private static SpatialAssistanceTarget BuildTarget()
        {
            var unit = new SemanticUnit("mwe:0:8", SemanticUnitKind.MultiwordExpression, 0, 8, "keep off", 2);
            var segment = new MixedLanguageSegment("keep off", "立ち入らない", true, unit);
            return new SpatialAssistanceTarget(
                segment,
                Array.Empty<OcrTextRegionSpan>(),
                SpatialAssistanceCoverage.Exact,
                new ViewportEnvelope(0.2, 0.3, 0.4, 0.5));
        }

        private sealed class ConstantRayProvider : IViewportRayProvider
        {
            private readonly SpatialVector3 direction;

            public ConstantRayProvider(SpatialVector3 direction)
            {
                this.direction = direction;
            }

            public bool TryCreateRay(ViewportPoint point, out SpatialRay ray)
            {
                ray = new SpatialRay(new SpatialVector3(0.0, 0.0, 0.0), direction);
                return true;
            }
        }

        private sealed class ConstantSurfaceRaycaster : ISurfaceRaycaster
        {
            private readonly SpatialVector3 normal;

            public ConstantSurfaceRaycaster(SpatialVector3 normal)
            {
                this.normal = normal;
            }

            public bool TryRaycast(SpatialRay ray, out SurfaceHit hit)
            {
                hit = new SurfaceHit(
                    new SpatialVector3(0.0, 0.0, 2.0),
                    normal,
                    2.0);
                return true;
            }
        }
    }
}
