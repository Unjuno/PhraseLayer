using PhraseLayer.Core.Inputs;
using PhraseLayer.Core.Spatial;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class SurfacePlaneTextLayoutTests
    {
        [Fact]
        public void EnvelopeCornersRecoverPhysicalPlaneExtentAndOrientation()
        {
            var projector = new SurfacePlaneTextLayoutProjector(new PerspectiveRayProvider());
            var surface = new SurfaceHit(
                new SpatialVector3(0.0, 0.0, 2.0),
                new SpatialVector3(0.0, 0.0, -1.0),
                2.0);

            var success = projector.TryProject(
                new ViewportEnvelope(0.25, 0.40, 0.75, 0.60),
                surface,
                out var layout,
                out var failure);

            Assert.True(success);
            Assert.Equal(SurfacePlaneLayoutFailure.None, failure);
            Assert.Equal(0.0, layout.Center.X, 6);
            Assert.Equal(0.0, layout.Center.Y, 6);
            Assert.Equal(2.0, layout.Center.Z, 6);
            Assert.Equal(1.0, layout.WidthMeters, 6);
            Assert.Equal(0.4, layout.HeightMeters, 6);
            Assert.Equal(1.0, layout.Right.X, 6);
            Assert.Equal(1.0, layout.Up.Y, 6);
            Assert.Equal(-1.0, layout.Normal.Z, 6);
        }

        [Fact]
        public void MissingCornerRayFailsWithoutInventingPlaneGeometry()
        {
            var projector = new SurfacePlaneTextLayoutProjector(new FailingRayProvider());
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
            Assert.Equal(SurfacePlaneLayoutFailure.ViewportRayUnavailable, failure);
        }

        [Fact]
        public void ParallelRayFailsClosed()
        {
            var projector = new SurfacePlaneTextLayoutProjector(new ConstantRayProvider(new SpatialVector3(1.0, 0.0, 0.0)));
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
            Assert.Equal(SurfacePlaneLayoutFailure.RayParallelToSurface, failure);
        }

        [Fact]
        public void SurfaceBehindRayFailsClosed()
        {
            var projector = new SurfacePlaneTextLayoutProjector(new ConstantRayProvider(new SpatialVector3(0.0, 0.0, -1.0)));
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
            Assert.Equal(SurfacePlaneLayoutFailure.SurfaceBehindRay, failure);
        }

        [Fact]
        public void ZeroWidthEnvelopeCannotBecomePhysicalTextExtent()
        {
            var projector = new SurfacePlaneTextLayoutProjector(new PerspectiveRayProvider());
            var surface = new SurfaceHit(
                new SpatialVector3(0.0, 0.0, 2.0),
                new SpatialVector3(0.0, 0.0, -1.0),
                2.0);

            var success = projector.TryProject(
                new ViewportEnvelope(0.50, 0.40, 0.50, 0.60),
                surface,
                out _,
                out var failure);

            Assert.False(success);
            Assert.Equal(SurfacePlaneLayoutFailure.DegenerateExtent, failure);
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

        private sealed class FailingRayProvider : IViewportRayProvider
        {
            public bool TryCreateRay(ViewportPoint point, out SpatialRay ray)
            {
                ray = default(SpatialRay);
                return false;
            }
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
    }
}
