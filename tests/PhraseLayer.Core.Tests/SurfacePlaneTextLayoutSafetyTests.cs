using System;
using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Spatial;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class SurfacePlaneTextLayoutSafetyTests
    {
        [Fact]
        public void NearParallelCornerThatExplodesPhysicalExtentFailsClosed()
        {
            var projector = new SurfacePlaneTextLayoutProjector(new ExtremeCornerRayProvider());
            var surface = new SurfaceHit(
                new SpatialVector3(0.0, 0.0, 2.0),
                new SpatialVector3(0.0, 0.0, -1.0),
                2.0);

            var success = projector.TryProject(
                new ViewportEnvelope(0.25, 0.40, 0.75, 0.60),
                surface,
                out _,
                out var failure);

            Assert.False(success);
            Assert.Equal(SurfacePlaneLayoutFailure.ImplausibleExtent, failure);
        }

        [Fact]
        public void SafetyCeilingCanBeConfiguredWithoutChangingGeometrySource()
        {
            var projector = new SurfacePlaneTextLayoutProjector(
                new PerspectiveRayProvider(),
                new SurfacePlaneTextLayoutProjectorOptions
                {
                    MaxCornerOffsetMultiplier = 0.01,
                    MaxCornerOffsetPaddingMeters = 0.01,
                });
            var surface = new SurfaceHit(
                new SpatialVector3(0.0, 0.0, 2.0),
                new SpatialVector3(0.0, 0.0, -1.0),
                2.0);

            var success = projector.TryProject(
                new ViewportEnvelope(0.25, 0.40, 0.75, 0.60),
                surface,
                out _,
                out var failure);

            Assert.False(success);
            Assert.Equal(SurfacePlaneLayoutFailure.ImplausibleExtent, failure);
        }

        [Fact]
        public void InvalidSafetyOptionsFailClosed()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SurfacePlaneTextLayoutProjector(
                new PerspectiveRayProvider(),
                new SurfacePlaneTextLayoutProjectorOptions { MaxCornerOffsetMultiplier = 0.0 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SurfacePlaneTextLayoutProjector(
                new PerspectiveRayProvider(),
                new SurfacePlaneTextLayoutProjectorOptions { MaxCornerOffsetPaddingMeters = -0.01 }));
        }

        private sealed class ExtremeCornerRayProvider : IViewportRayProvider
        {
            public bool TryCreateRay(ViewportPoint point, out SpatialRay ray)
            {
                var direction = point.U < 0.30
                    ? new SpatialVector3(1.0, 0.0, 0.001)
                    : new SpatialVector3(point.U - 0.5, point.V - 0.5, 1.0);
                ray = new SpatialRay(new SpatialVector3(0.0, 0.0, 0.0), direction);
                return true;
            }
        }

        private sealed class PerspectiveRayProvider : IViewportRayProvider
        {
            public bool TryCreateRay(ViewportPoint point, out SpatialRay ray)
            {
                ray = new SpatialRay(
                    new SpatialVector3(0.0, 0.0, 0.0),
                    new SpatialVector3(point.U - 0.5, point.V - 0.5, 1.0));
                return true;
            }
        }
    }
}
