using System;
using PhraseLayer.Core.Inputs;

namespace PhraseLayer.Core.Spatial
{
    public enum SurfacePlaneLayoutFailure
    {
        None = 0,
        ViewportRayUnavailable = 1,
        RayParallelToSurface = 2,
        SurfaceBehindRay = 3,
        DegenerateExtent = 4,
        ImplausibleExtent = 5
    }

    public sealed class SurfacePlaneTextLayoutProjectorOptions
    {
        public SurfacePlaneTextLayoutProjectorOptions()
        {
            MaxCornerOffsetMultiplier = 2.0;
            MaxCornerOffsetPaddingMeters = 0.5;
        }

        /// <summary>
        /// Maximum accepted corner displacement from the verified center hit, relative to that hit's camera distance.
        /// This is a generous safety ceiling, not a guessed text size.
        /// </summary>
        public double MaxCornerOffsetMultiplier { get; set; }

        /// <summary>
        /// Additional absolute allowance for nearby text where a pure distance ratio would be too restrictive.
        /// </summary>
        public double MaxCornerOffsetPaddingMeters { get; set; }

        internal void Validate()
        {
            if (double.IsNaN(MaxCornerOffsetMultiplier) || double.IsInfinity(MaxCornerOffsetMultiplier) ||
                MaxCornerOffsetMultiplier <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(MaxCornerOffsetMultiplier));
            if (double.IsNaN(MaxCornerOffsetPaddingMeters) || double.IsInfinity(MaxCornerOffsetPaddingMeters) ||
                MaxCornerOffsetPaddingMeters < 0.0)
                throw new ArgumentOutOfRangeException(nameof(MaxCornerOffsetPaddingMeters));
        }
    }

    /// <summary>
    /// Physical layout of a viewport-aligned text envelope on one already-verified world surface plane.
    /// All vectors and dimensions are expressed in the same world/tracking space as the source SurfaceHit.
    /// </summary>
    public readonly struct SurfaceTextLayout
    {
        public SurfaceTextLayout(
            SpatialVector3 center,
            SpatialVector3 right,
            SpatialVector3 up,
            SpatialVector3 normal,
            double widthMeters,
            double heightMeters)
        {
            if (widthMeters <= 0.0 || double.IsNaN(widthMeters) || double.IsInfinity(widthMeters))
                throw new ArgumentOutOfRangeException(nameof(widthMeters));
            if (heightMeters <= 0.0 || double.IsNaN(heightMeters) || double.IsInfinity(heightMeters))
                throw new ArgumentOutOfRangeException(nameof(heightMeters));
            if (right.SquaredMagnitude <= 0.0) throw new ArgumentException("Right vector must be non-zero.", nameof(right));
            if (up.SquaredMagnitude <= 0.0) throw new ArgumentException("Up vector must be non-zero.", nameof(up));
            if (normal.SquaredMagnitude <= 0.0) throw new ArgumentException("Normal vector must be non-zero.", nameof(normal));

            Center = center;
            Right = right;
            Up = up;
            Normal = normal;
            WidthMeters = widthMeters;
            HeightMeters = heightMeters;
        }

        public SpatialVector3 Center { get; }
        public SpatialVector3 Right { get; }
        public SpatialVector3 Up { get; }
        public SpatialVector3 Normal { get; }
        public double WidthMeters { get; }
        public double HeightMeters { get; }
    }

    /// <summary>
    /// Projects a stabilized viewport envelope onto the tangent plane of an already-verified SurfaceHit.
    /// This never invents depth: every corner comes from IViewportRayProvider and intersects the supplied real hit
    /// plane. Failure leaves the caller free to keep a conservative center-only world label or viewport fallback.
    /// Near-parallel corner rays are additionally bounded against the verified center hit so a mathematically valid
    /// but physically implausible far-away intersection cannot inflate a Read label across the room.
    /// </summary>
    public sealed class SurfacePlaneTextLayoutProjector
    {
        private const double ParallelTolerance = 1e-8;
        private const double MinimumExtentMeters = 1e-5;
        private readonly IViewportRayProvider rayProvider;
        private readonly SurfacePlaneTextLayoutProjectorOptions options;

        public SurfacePlaneTextLayoutProjector(
            IViewportRayProvider rayProvider,
            SurfacePlaneTextLayoutProjectorOptions? options = null)
        {
            this.rayProvider = rayProvider ?? throw new ArgumentNullException(nameof(rayProvider));
            this.options = options ?? new SurfacePlaneTextLayoutProjectorOptions();
            this.options.Validate();
        }

        public bool TryProject(
            ViewportEnvelope envelope,
            SurfaceHit surface,
            out SurfaceTextLayout layout,
            out SurfacePlaneLayoutFailure failure)
        {
            layout = default(SurfaceTextLayout);
            failure = SurfacePlaneLayoutFailure.None;

            var normal = Normalize(surface.Normal);
            var viewportCorners = new[]
            {
                new ViewportPoint(envelope.MinU, envelope.MinV),
                new ViewportPoint(envelope.MaxU, envelope.MinV),
                new ViewportPoint(envelope.MaxU, envelope.MaxV),
                new ViewportPoint(envelope.MinU, envelope.MaxV),
            };
            var worldCorners = new SpatialVector3[viewportCorners.Length];
            var maxCornerOffset = (surface.DistanceMeters * options.MaxCornerOffsetMultiplier) +
                                  options.MaxCornerOffsetPaddingMeters;

            for (var index = 0; index < viewportCorners.Length; index++)
            {
                if (!rayProvider.TryCreateRay(viewportCorners[index], out var ray))
                {
                    failure = SurfacePlaneLayoutFailure.ViewportRayUnavailable;
                    return false;
                }

                if (!TryIntersectPlane(ray, surface.Point, normal, out worldCorners[index], out failure))
                    return false;

                if (Distance(worldCorners[index], surface.Point) > maxCornerOffset)
                {
                    failure = SurfacePlaneLayoutFailure.ImplausibleExtent;
                    return false;
                }
            }

            var leftCenter = Midpoint(worldCorners[0], worldCorners[3]);
            var rightCenter = Midpoint(worldCorners[1], worldCorners[2]);
            var bottomCenter = Midpoint(worldCorners[0], worldCorners[1]);
            var topCenter = Midpoint(worldCorners[3], worldCorners[2]);
            var horizontal = Subtract(rightCenter, leftCenter);
            var vertical = Subtract(topCenter, bottomCenter);
            var width = Magnitude(horizontal);
            var height = Magnitude(vertical);

            if (width <= MinimumExtentMeters || height <= MinimumExtentMeters)
            {
                failure = SurfacePlaneLayoutFailure.DegenerateExtent;
                return false;
            }

            var center = Average(worldCorners);
            layout = new SurfaceTextLayout(
                center,
                Normalize(horizontal),
                Normalize(vertical),
                normal,
                width,
                height);
            return true;
        }

        private static bool TryIntersectPlane(
            SpatialRay ray,
            SpatialVector3 planePoint,
            SpatialVector3 planeNormal,
            out SpatialVector3 point,
            out SurfacePlaneLayoutFailure failure)
        {
            point = default(SpatialVector3);
            failure = SurfacePlaneLayoutFailure.None;

            var denominator = Dot(ray.Direction, planeNormal);
            if (Math.Abs(denominator) <= ParallelTolerance)
            {
                failure = SurfacePlaneLayoutFailure.RayParallelToSurface;
                return false;
            }

            var distanceAlongRay = Dot(Subtract(planePoint, ray.Origin), planeNormal) / denominator;
            if (distanceAlongRay < 0.0)
            {
                failure = SurfacePlaneLayoutFailure.SurfaceBehindRay;
                return false;
            }

            point = new SpatialVector3(
                ray.Origin.X + (ray.Direction.X * distanceAlongRay),
                ray.Origin.Y + (ray.Direction.Y * distanceAlongRay),
                ray.Origin.Z + (ray.Direction.Z * distanceAlongRay));
            return true;
        }

        private static SpatialVector3 Average(SpatialVector3[] values)
        {
            var x = 0.0;
            var y = 0.0;
            var z = 0.0;
            for (var index = 0; index < values.Length; index++)
            {
                x += values[index].X;
                y += values[index].Y;
                z += values[index].Z;
            }
            return new SpatialVector3(x / values.Length, y / values.Length, z / values.Length);
        }

        private static SpatialVector3 Midpoint(SpatialVector3 left, SpatialVector3 right)
        {
            return new SpatialVector3(
                (left.X + right.X) * 0.5,
                (left.Y + right.Y) * 0.5,
                (left.Z + right.Z) * 0.5);
        }

        private static SpatialVector3 Subtract(SpatialVector3 left, SpatialVector3 right)
        {
            return new SpatialVector3(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
        }

        private static double Dot(SpatialVector3 left, SpatialVector3 right)
        {
            return (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);
        }

        private static double Distance(SpatialVector3 left, SpatialVector3 right)
        {
            return Magnitude(Subtract(left, right));
        }

        private static double Magnitude(SpatialVector3 value)
        {
            return Math.Sqrt(value.SquaredMagnitude);
        }

        private static SpatialVector3 Normalize(SpatialVector3 value)
        {
            var magnitude = Magnitude(value);
            if (magnitude <= 0.0)
                throw new ArgumentException("Surface-plane layout vector must be non-zero.", nameof(value));
            return new SpatialVector3(value.X / magnitude, value.Y / magnitude, value.Z / magnitude);
        }
    }
}
