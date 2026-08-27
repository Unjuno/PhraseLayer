using System;
using System.Collections.Generic;
using System.Linq;
using PhraseLayer.Core.Inputs;

namespace PhraseLayer.Core.Spatial
{
    public enum WorldTextLayoutFailure
    {
        None = 0,
        NotInPlaceReplacement = 1,
        MissingEnvelope = 2,
        ViewportRayUnavailable = 3,
        SurfaceNotFound = 4,
        DegenerateGeometry = 5,
        InconsistentSurfaceNormals = 6,
        NonPlanarSurface = 7
    }

    /// <summary>
    /// A fitted physical text plane derived from four independent viewport-corner surface samples.
    /// Right and Up preserve the viewport text orientation. Normal is the corresponding right-handed
    /// layout normal; the raw collider normal sign may therefore be flipped without changing the plane.
    /// WidthMeters/HeightMeters describe the fitted OCR envelope extent on that plane.
    /// </summary>
    public readonly struct WorldTextSurface
    {
        public WorldTextSurface(
            SpatialVector3 center,
            SpatialVector3 right,
            SpatialVector3 up,
            SpatialVector3 normal,
            double widthMeters,
            double heightMeters,
            double maxPlanarityErrorMeters)
        {
            if (!SpatialMath.IsFiniteVector(center))
                throw new ArgumentException("World text surface center must be finite.", nameof(center));
            if (widthMeters <= 0.0 || double.IsNaN(widthMeters) || double.IsInfinity(widthMeters))
                throw new ArgumentOutOfRangeException(nameof(widthMeters));
            if (heightMeters <= 0.0 || double.IsNaN(heightMeters) || double.IsInfinity(heightMeters))
                throw new ArgumentOutOfRangeException(nameof(heightMeters));
            if (maxPlanarityErrorMeters < 0.0 || double.IsNaN(maxPlanarityErrorMeters) || double.IsInfinity(maxPlanarityErrorMeters))
                throw new ArgumentOutOfRangeException(nameof(maxPlanarityErrorMeters));
            if (!SpatialMath.IsUnit(right) || !SpatialMath.IsUnit(up) || !SpatialMath.IsUnit(normal))
                throw new ArgumentException("World text surface axes must be finite unit vectors.");
            if (Math.Abs(SpatialMath.Dot(right, up)) > 1e-5 ||
                Math.Abs(SpatialMath.Dot(right, normal)) > 1e-5 ||
                Math.Abs(SpatialMath.Dot(up, normal)) > 1e-5)
            {
                throw new ArgumentException("World text surface axes must be mutually orthogonal.");
            }

            Center = center;
            Right = right;
            Up = up;
            Normal = normal;
            WidthMeters = widthMeters;
            HeightMeters = heightMeters;
            MaxPlanarityErrorMeters = maxPlanarityErrorMeters;
        }

        public SpatialVector3 Center { get; }
        public SpatialVector3 Right { get; }
        public SpatialVector3 Up { get; }
        public SpatialVector3 Normal { get; }
        public double WidthMeters { get; }
        public double HeightMeters { get; }
        public double MaxPlanarityErrorMeters { get; }
    }

    public sealed class WorldTextLayoutTarget
    {
        public WorldTextLayoutTarget(
            ProjectedAssistanceTarget source,
            WorldTextLayoutFailure failure,
            WorldTextSurface? surface)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Failure = failure;
            Surface = surface;
        }

        public ProjectedAssistanceTarget Source { get; }
        public WorldTextLayoutFailure Failure { get; }
        public WorldTextSurface? Surface { get; }
        public bool IsReady => Failure == WorldTextLayoutFailure.None && Surface.HasValue;
    }

    public sealed class WorldTextLayoutPlan
    {
        public WorldTextLayoutPlan(IReadOnlyList<WorldTextLayoutTarget> targets)
        {
            Targets = targets ?? throw new ArgumentNullException(nameof(targets));
        }

        public IReadOnlyList<WorldTextLayoutTarget> Targets { get; }
        public int ReadyCount => Targets.Count(item => item.IsReady);
        public int FailedCount => Targets.Count - ReadyCount;
    }

    /// <summary>
    /// Fits a physical text plane from the four corners of an OCR semantic envelope.
    /// Only targets already approved for InPlaceReplacement are fitted. Every corner must produce
    /// both a Passthrough-camera viewport ray and a physical surface hit; missing geometry is never guessed.
    /// </summary>
    public sealed class WorldTextLayoutPlanner
    {
        private readonly IViewportRayProvider rayProvider;
        private readonly ISurfaceRaycaster raycaster;
        private readonly double maximumPlanarityErrorMeters;
        private readonly double minimumExtentMeters;
        private readonly double minimumNormalDot;

        public WorldTextLayoutPlanner(
            IViewportRayProvider rayProvider,
            ISurfaceRaycaster raycaster,
            double maximumPlanarityErrorMeters = 0.03,
            double minimumExtentMeters = 0.005,
            double minimumNormalDot = 0.80)
        {
            this.rayProvider = rayProvider ?? throw new ArgumentNullException(nameof(rayProvider));
            this.raycaster = raycaster ?? throw new ArgumentNullException(nameof(raycaster));
            if (!IsFinitePositive(maximumPlanarityErrorMeters))
                throw new ArgumentOutOfRangeException(nameof(maximumPlanarityErrorMeters));
            if (!IsFinitePositive(minimumExtentMeters))
                throw new ArgumentOutOfRangeException(nameof(minimumExtentMeters));
            if (double.IsNaN(minimumNormalDot) || double.IsInfinity(minimumNormalDot) || minimumNormalDot < 0.0 || minimumNormalDot > 1.0)
                throw new ArgumentOutOfRangeException(nameof(minimumNormalDot));

            this.maximumPlanarityErrorMeters = maximumPlanarityErrorMeters;
            this.minimumExtentMeters = minimumExtentMeters;
            this.minimumNormalDot = minimumNormalDot;
        }

        public double MaximumPlanarityErrorMeters => maximumPlanarityErrorMeters;
        public double MinimumExtentMeters => minimumExtentMeters;
        public double MinimumNormalDot => minimumNormalDot;

        public WorldTextLayoutPlan Fit(SpatialProjectionPlan projection)
        {
            if (projection == null) throw new ArgumentNullException(nameof(projection));
            var targets = new List<WorldTextLayoutTarget>(projection.Targets.Count);
            foreach (var target in projection.Targets)
                targets.Add(FitOne(target));
            return new WorldTextLayoutPlan(targets);
        }

        private WorldTextLayoutTarget FitOne(ProjectedAssistanceTarget target)
        {
            if (target.PlacementKind != OverlayPlacementKind.InPlaceReplacement || !target.CanRenderInWorld)
                return Failed(target, WorldTextLayoutFailure.NotInPlaceReplacement);
            if (!target.Source.Envelope.HasValue)
                return Failed(target, WorldTextLayoutFailure.MissingEnvelope);

            var envelope = target.Source.Envelope.Value;
            var viewportCorners = new[]
            {
                new ViewportPoint(envelope.MinU, envelope.MinV),
                new ViewportPoint(envelope.MaxU, envelope.MinV),
                new ViewportPoint(envelope.MaxU, envelope.MaxV),
                new ViewportPoint(envelope.MinU, envelope.MaxV)
            };
            var hits = new SurfaceHit[4];

            for (var index = 0; index < viewportCorners.Length; index++)
            {
                if (!rayProvider.TryCreateRay(viewportCorners[index], out var ray))
                    return Failed(target, WorldTextLayoutFailure.ViewportRayUnavailable);
                if (!raycaster.TryRaycast(ray, out hits[index]))
                    return Failed(target, WorldTextLayoutFailure.SurfaceNotFound);
            }

            if (!TryAverageNormal(hits, out var normal))
                return Failed(target, WorldTextLayoutFailure.InconsistentSurfaceNormals);

            var center = SpatialMath.Scale(
                SpatialMath.Add(
                    SpatialMath.Add(hits[0].Point, hits[1].Point),
                    SpatialMath.Add(hits[2].Point, hits[3].Point)),
                0.25);

            var observedRight = SpatialMath.Scale(
                SpatialMath.Add(
                    SpatialMath.Subtract(hits[1].Point, hits[0].Point),
                    SpatialMath.Subtract(hits[2].Point, hits[3].Point)),
                0.5);
            var rightOnPlane = SpatialMath.Reject(observedRight, normal);
            if (!SpatialMath.TryNormalize(rightOnPlane, out var right))
                return Failed(target, WorldTextLayoutFailure.DegenerateGeometry);

            var observedUp = SpatialMath.Scale(
                SpatialMath.Add(
                    SpatialMath.Subtract(hits[3].Point, hits[0].Point),
                    SpatialMath.Subtract(hits[2].Point, hits[1].Point)),
                0.5);
            var upOnPlane = SpatialMath.Reject(observedUp, normal);
            upOnPlane = SpatialMath.Reject(upOnPlane, right);
            if (!SpatialMath.TryNormalize(upOnPlane, out var up))
            {
                var fallbackUp = SpatialMath.Cross(normal, right);
                if (!SpatialMath.TryNormalize(fallbackUp, out up))
                    return Failed(target, WorldTextLayoutFailure.DegenerateGeometry);
                if (SpatialMath.Dot(up, observedUp) < 0.0)
                    up = SpatialMath.Scale(up, -1.0);
            }

            // Surface normal sign is arbitrary for a plane. Preserve viewport right/up and flip only the
            // fitted layout normal so downstream rendering never silently mirrors or inverts recognized text.
            if (SpatialMath.Dot(SpatialMath.Cross(right, up), normal) < 0.0)
                normal = SpatialMath.Scale(normal, -1.0);

            var widthMeters = 0.5 * (
                Math.Abs(SpatialMath.Dot(SpatialMath.Subtract(hits[1].Point, hits[0].Point), right)) +
                Math.Abs(SpatialMath.Dot(SpatialMath.Subtract(hits[2].Point, hits[3].Point), right)));
            var heightMeters = 0.5 * (
                Math.Abs(SpatialMath.Dot(SpatialMath.Subtract(hits[3].Point, hits[0].Point), up)) +
                Math.Abs(SpatialMath.Dot(SpatialMath.Subtract(hits[2].Point, hits[1].Point), up)));

            if (widthMeters < minimumExtentMeters || heightMeters < minimumExtentMeters)
                return Failed(target, WorldTextLayoutFailure.DegenerateGeometry);

            var maximumResidual = 0.0;
            for (var index = 0; index < hits.Length; index++)
            {
                var residual = Math.Abs(SpatialMath.Dot(SpatialMath.Subtract(hits[index].Point, center), normal));
                if (residual > maximumResidual) maximumResidual = residual;
            }
            if (maximumResidual > maximumPlanarityErrorMeters)
                return Failed(target, WorldTextLayoutFailure.NonPlanarSurface);

            return new WorldTextLayoutTarget(
                target,
                WorldTextLayoutFailure.None,
                new WorldTextSurface(
                    center,
                    right,
                    up,
                    normal,
                    widthMeters,
                    heightMeters,
                    maximumResidual));
        }

        private bool TryAverageNormal(IReadOnlyList<SurfaceHit> hits, out SpatialVector3 averageNormal)
        {
            var sum = new SpatialVector3(0.0, 0.0, 0.0);
            var normalized = new SpatialVector3[hits.Count];
            for (var index = 0; index < hits.Count; index++)
            {
                if (!SpatialMath.TryNormalize(hits[index].Normal, out normalized[index]))
                {
                    averageNormal = default(SpatialVector3);
                    return false;
                }
                sum = SpatialMath.Add(sum, normalized[index]);
            }

            if (!SpatialMath.TryNormalize(sum, out averageNormal))
                return false;

            for (var index = 0; index < normalized.Length; index++)
            {
                if (SpatialMath.Dot(normalized[index], averageNormal) < minimumNormalDot)
                    return false;
            }
            return true;
        }

        private static WorldTextLayoutTarget Failed(ProjectedAssistanceTarget target, WorldTextLayoutFailure failure)
        {
            return new WorldTextLayoutTarget(target, failure, null);
        }

        private static bool IsFinitePositive(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0;
        }
    }

    internal static class SpatialMath
    {
        private const double UnitTolerance = 1e-6;

        public static SpatialVector3 Add(SpatialVector3 a, SpatialVector3 b) =>
            new SpatialVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static SpatialVector3 Subtract(SpatialVector3 a, SpatialVector3 b) =>
            new SpatialVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static SpatialVector3 Scale(SpatialVector3 value, double scale) =>
            new SpatialVector3(value.X * scale, value.Y * scale, value.Z * scale);

        public static double Dot(SpatialVector3 a, SpatialVector3 b) =>
            (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

        public static SpatialVector3 Cross(SpatialVector3 a, SpatialVector3 b) =>
            new SpatialVector3(
                (a.Y * b.Z) - (a.Z * b.Y),
                (a.Z * b.X) - (a.X * b.Z),
                (a.X * b.Y) - (a.Y * b.X));

        public static SpatialVector3 Reject(SpatialVector3 value, SpatialVector3 normalUnit) =>
            Subtract(value, Scale(normalUnit, Dot(value, normalUnit)));

        public static bool TryNormalize(SpatialVector3 value, out SpatialVector3 normalized)
        {
            var squared = value.SquaredMagnitude;
            if (double.IsNaN(squared) || double.IsInfinity(squared) || squared <= 1e-18)
            {
                normalized = default(SpatialVector3);
                return false;
            }

            var magnitude = Math.Sqrt(squared);
            normalized = Scale(value, 1.0 / magnitude);
            return IsFiniteVector(normalized);
        }

        public static bool IsUnit(SpatialVector3 value)
        {
            if (!IsFiniteVector(value)) return false;
            return Math.Abs(value.SquaredMagnitude - 1.0) <= UnitTolerance;
        }

        public static bool IsFiniteVector(SpatialVector3 value)
        {
            return IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
