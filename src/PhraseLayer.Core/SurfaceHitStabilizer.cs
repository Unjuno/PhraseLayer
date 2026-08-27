using System;
using System.Collections.Generic;

namespace PhraseLayer.Core.Spatial
{
    /// <summary>
    /// Configuration for conservative world-space surface-hit stabilization.
    /// Small point/normal jitter is blended, while a larger displacement or surface-normal change is accepted
    /// immediately so real head/object motion does not leave the overlay attached to stale geometry.
    /// </summary>
    public sealed class SurfaceHitStabilizerOptions
    {
        public SurfaceHitStabilizerOptions()
        {
            BlendFactor = 0.35;
            ResetPointDistanceMeters = 0.20;
            ResetNormalAngleDegrees = 20.0;
            MaxMissingObservations = 1;
        }

        /// <summary>
        /// Fraction of the newest verified surface hit applied for small movements. 1.0 disables smoothing.
        /// </summary>
        public double BlendFactor { get; set; }

        /// <summary>
        /// World-space point displacement above which the new hit is accepted immediately.
        /// </summary>
        public double ResetPointDistanceMeters { get; set; }

        /// <summary>
        /// Surface-normal angular change above which the new hit is accepted immediately.
        /// </summary>
        public double ResetNormalAngleDegrees { get; set; }

        /// <summary>
        /// Number of consecutive raycast misses for which a previously verified surface hit may be retained.
        /// Zero disables miss retention.
        /// </summary>
        public int MaxMissingObservations { get; set; }

        internal void Validate()
        {
            if (double.IsNaN(BlendFactor) || double.IsInfinity(BlendFactor) || BlendFactor <= 0.0 || BlendFactor > 1.0)
                throw new ArgumentOutOfRangeException(nameof(BlendFactor));
            if (double.IsNaN(ResetPointDistanceMeters) || double.IsInfinity(ResetPointDistanceMeters) || ResetPointDistanceMeters < 0.0)
                throw new ArgumentOutOfRangeException(nameof(ResetPointDistanceMeters));
            if (double.IsNaN(ResetNormalAngleDegrees) || double.IsInfinity(ResetNormalAngleDegrees) ||
                ResetNormalAngleDegrees < 0.0 || ResetNormalAngleDegrees > 180.0)
                throw new ArgumentOutOfRangeException(nameof(ResetNormalAngleDegrees));
            if (MaxMissingObservations < 0)
                throw new ArgumentOutOfRangeException(nameof(MaxMissingObservations));
        }
    }

    /// <summary>
    /// Per-semantic-target temporal stabilization for already-verified world-space surface hits.
    ///
    /// This class never creates geometry and never upgrades an unresolved target into a world placement. It only
    /// smooths hits that came from an ISurfaceRaycaster and may retain the last verified hit for a bounded number of
    /// misses. Call Reset when the Read encounter changes so world geometry cannot leak between physical texts.
    /// </summary>
    public sealed class SurfaceHitStabilizer
    {
        private sealed class HitState
        {
            public HitState(SurfaceHit hit)
            {
                Hit = hit;
                MissingObservations = 0;
            }

            public SurfaceHit Hit { get; set; }
            public int MissingObservations { get; set; }
        }

        private readonly SurfaceHitStabilizerOptions options;
        private readonly Dictionary<string, HitState> states =
            new Dictionary<string, HitState>(StringComparer.Ordinal);

        public SurfaceHitStabilizer(SurfaceHitStabilizerOptions? options = null)
        {
            this.options = options ?? new SurfaceHitStabilizerOptions();
            this.options.Validate();
        }

        public int Count => states.Count;

        public SurfaceHit Stabilize(string key, SurfaceHit observed)
        {
            ValidateKey(key);

            if (!states.TryGetValue(key, out var state))
            {
                states[key] = new HitState(observed);
                return observed;
            }

            var pointDistance = Distance(state.Hit.Point, observed.Point);
            var normalAngle = NormalAngleDegrees(state.Hit.Normal, observed.Normal);
            var stabilized = pointDistance > options.ResetPointDistanceMeters ||
                             normalAngle > options.ResetNormalAngleDegrees
                ? observed
                : Blend(state.Hit, observed, options.BlendFactor);

            state.Hit = stabilized;
            state.MissingObservations = 0;
            return stabilized;
        }

        /// <summary>
        /// Returns the last verified hit for a bounded number of consecutive misses. Once the budget is exhausted the
        /// state is removed so a stale physical surface cannot remain visible indefinitely.
        /// </summary>
        public bool TryHoldMissing(string key, out SurfaceHit hit)
        {
            ValidateKey(key);

            if (!states.TryGetValue(key, out var state) || options.MaxMissingObservations == 0)
            {
                hit = default(SurfaceHit);
                return false;
            }

            state.MissingObservations++;
            if (state.MissingObservations > options.MaxMissingObservations)
            {
                states.Remove(key);
                hit = default(SurfaceHit);
                return false;
            }

            hit = state.Hit;
            return true;
        }

        public bool TryGet(string key, out SurfaceHit hit)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (states.TryGetValue(key, out var state))
            {
                hit = state.Hit;
                return true;
            }

            hit = default(SurfaceHit);
            return false;
        }

        public void Reset()
        {
            states.Clear();
        }

        private static void ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("A stable semantic target key is required.", nameof(key));
        }

        private static SurfaceHit Blend(SurfaceHit previous, SurfaceHit observed, double factor)
        {
            var point = new SpatialVector3(
                Lerp(previous.Point.X, observed.Point.X, factor),
                Lerp(previous.Point.Y, observed.Point.Y, factor),
                Lerp(previous.Point.Z, observed.Point.Z, factor));

            var previousNormal = Normalize(previous.Normal);
            var observedNormal = Normalize(observed.Normal);
            var normal = Normalize(new SpatialVector3(
                Lerp(previousNormal.X, observedNormal.X, factor),
                Lerp(previousNormal.Y, observedNormal.Y, factor),
                Lerp(previousNormal.Z, observedNormal.Z, factor)));

            return new SurfaceHit(
                point,
                normal,
                Lerp(previous.DistanceMeters, observed.DistanceMeters, factor));
        }

        private static double NormalAngleDegrees(SpatialVector3 left, SpatialVector3 right)
        {
            var normalizedLeft = Normalize(left);
            var normalizedRight = Normalize(right);
            var dot = (normalizedLeft.X * normalizedRight.X) +
                      (normalizedLeft.Y * normalizedRight.Y) +
                      (normalizedLeft.Z * normalizedRight.Z);
            dot = Math.Max(-1.0, Math.Min(1.0, dot));
            return Math.Acos(dot) * (180.0 / Math.PI);
        }

        private static SpatialVector3 Normalize(SpatialVector3 value)
        {
            var magnitude = Math.Sqrt(value.SquaredMagnitude);
            if (magnitude <= 0.0)
                throw new ArgumentException("Surface normal must be non-zero.", nameof(value));
            return new SpatialVector3(value.X / magnitude, value.Y / magnitude, value.Z / magnitude);
        }

        private static double Distance(SpatialVector3 left, SpatialVector3 right)
        {
            var dx = left.X - right.X;
            var dy = left.Y - right.Y;
            var dz = left.Z - right.Z;
            return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        private static double Lerp(double from, double to, double factor)
        {
            return from + ((to - from) * factor);
        }
    }
}
