using System;
using System.Collections.Generic;
using System.Linq;
using PhraseLayer.Core.Inputs;

namespace PhraseLayer.Core.Spatial
{
    public readonly struct SpatialVector3
    {
        public SpatialVector3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public double SquaredMagnitude => (X * X) + (Y * Y) + (Z * Z);
    }

    public readonly struct SpatialRay
    {
        public SpatialRay(SpatialVector3 origin, SpatialVector3 direction)
        {
            if (direction.SquaredMagnitude <= 0.0)
                throw new ArgumentException("Ray direction must be non-zero.", nameof(direction));
            Origin = origin;
            Direction = direction;
        }

        public SpatialVector3 Origin { get; }
        public SpatialVector3 Direction { get; }
    }

    public readonly struct SurfaceHit
    {
        public SurfaceHit(SpatialVector3 point, SpatialVector3 normal, double distanceMeters)
        {
            if (normal.SquaredMagnitude <= 0.0)
                throw new ArgumentException("Surface normal must be non-zero.", nameof(normal));
            if (distanceMeters < 0.0)
                throw new ArgumentOutOfRangeException(nameof(distanceMeters));
            Point = point;
            Normal = normal;
            DistanceMeters = distanceMeters;
        }

        public SpatialVector3 Point { get; }
        public SpatialVector3 Normal { get; }
        public double DistanceMeters { get; }
    }

    public interface IViewportRayProvider
    {
        bool TryCreateRay(ViewportPoint point, out SpatialRay ray);
    }

    public interface ISurfaceRaycaster
    {
        bool TryRaycast(SpatialRay ray, out SurfaceHit hit);
    }

    public enum OverlayPlacementKind
    {
        Skip = 0,
        AdjacentLabel = 1,
        InPlaceReplacement = 2
    }

    public enum SpatialProjectionFailure
    {
        None = 0,
        NoReliableGeometry = 1,
        ViewportRayUnavailable = 2,
        SurfaceNotFound = 3
    }

    public sealed class ProjectedAssistanceTarget
    {
        public ProjectedAssistanceTarget(
            SpatialAssistanceTarget source,
            OverlayPlacementKind placementKind,
            SpatialProjectionFailure failure,
            ViewportPoint? viewportAnchor,
            SpatialRay? ray,
            SurfaceHit? surface)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            PlacementKind = placementKind;
            Failure = failure;
            ViewportAnchor = viewportAnchor;
            Ray = ray;
            Surface = surface;
        }

        public SpatialAssistanceTarget Source { get; }
        public OverlayPlacementKind PlacementKind { get; }
        public SpatialProjectionFailure Failure { get; }
        public ViewportPoint? ViewportAnchor { get; }
        public SpatialRay? Ray { get; }
        public SurfaceHit? Surface { get; }
        public bool CanRenderInWorld => Failure == SpatialProjectionFailure.None && Surface.HasValue;
    }

    public sealed class SpatialProjectionPlan
    {
        public SpatialProjectionPlan(IReadOnlyList<ProjectedAssistanceTarget> targets)
        {
            Targets = targets ?? throw new ArgumentNullException(nameof(targets));
        }

        public IReadOnlyList<ProjectedAssistanceTarget> Targets { get; }
        public int InPlaceCount => Targets.Count(item => item.PlacementKind == OverlayPlacementKind.InPlaceReplacement);
        public int AdjacentCount => Targets.Count(item => item.PlacementKind == OverlayPlacementKind.AdjacentLabel);
        public int SkippedCount => Targets.Count(item => item.PlacementKind == OverlayPlacementKind.Skip);
    }

    /// <summary>
    /// Conservative placement policy: only Exact OCR coverage may replace source text.
    /// Partial coverage may render a nearby label. Unresolved geometry is never placed by guessing.
    /// Verified surface normals are canonicalized to face back toward the camera ray origin so renderer behavior does
    /// not depend on whether a particular geometry backend reports the same plane normal with the opposite sign.
    /// </summary>
    public sealed class SpatialProjectionPlanner
    {
        private readonly IViewportRayProvider rayProvider;
        private readonly ISurfaceRaycaster raycaster;

        public SpatialProjectionPlanner(IViewportRayProvider rayProvider, ISurfaceRaycaster raycaster)
        {
            this.rayProvider = rayProvider ?? throw new ArgumentNullException(nameof(rayProvider));
            this.raycaster = raycaster ?? throw new ArgumentNullException(nameof(raycaster));
        }

        public SpatialProjectionPlan Project(SpatialAssistancePlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var output = new List<ProjectedAssistanceTarget>(plan.Targets.Count);
            foreach (var target in plan.Targets)
                output.Add(ProjectOne(target));
            return new SpatialProjectionPlan(output);
        }

        private ProjectedAssistanceTarget ProjectOne(SpatialAssistanceTarget target)
        {
            if (target.Coverage == SpatialAssistanceCoverage.Unresolved || !target.Envelope.HasValue)
            {
                return new ProjectedAssistanceTarget(
                    target,
                    OverlayPlacementKind.Skip,
                    SpatialProjectionFailure.NoReliableGeometry,
                    null,
                    null,
                    null);
            }

            var anchor = target.Envelope.Value.Center;
            if (!rayProvider.TryCreateRay(anchor, out var ray))
            {
                return new ProjectedAssistanceTarget(
                    target,
                    OverlayPlacementKind.Skip,
                    SpatialProjectionFailure.ViewportRayUnavailable,
                    anchor,
                    null,
                    null);
            }

            if (!raycaster.TryRaycast(ray, out var hit))
            {
                return new ProjectedAssistanceTarget(
                    target,
                    OverlayPlacementKind.Skip,
                    SpatialProjectionFailure.SurfaceNotFound,
                    anchor,
                    ray,
                    null);
            }

            hit = OrientSurfaceTowardRayOrigin(ray, hit);
            var kind = target.Coverage == SpatialAssistanceCoverage.Exact
                ? OverlayPlacementKind.InPlaceReplacement
                : OverlayPlacementKind.AdjacentLabel;

            return new ProjectedAssistanceTarget(
                target,
                kind,
                SpatialProjectionFailure.None,
                anchor,
                ray,
                hit);
        }

        private static SurfaceHit OrientSurfaceTowardRayOrigin(SpatialRay ray, SurfaceHit hit)
        {
            if (Dot(ray.Direction, hit.Normal) <= 0.0)
                return hit;

            return new SurfaceHit(
                hit.Point,
                new SpatialVector3(-hit.Normal.X, -hit.Normal.Y, -hit.Normal.Z),
                hit.DistanceMeters);
        }

        private static double Dot(SpatialVector3 left, SpatialVector3 right)
        {
            return (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);
        }
    }
}
