using System;
using System.Collections.Generic;
using System.Linq;

namespace PhraseLayer.Core.Spatial
{
    public sealed class WorldTextTrackState
    {
        public WorldTextTrackState(
            long trackId,
            WorldTextLayoutTarget source,
            WorldTextSurface surface,
            bool observedThisFrame,
            long firstSeenTimestampMicroseconds,
            long lastSeenTimestampMicroseconds,
            int observationCount)
        {
            if (trackId <= 0) throw new ArgumentOutOfRangeException(nameof(trackId));
            Source = source ?? throw new ArgumentNullException(nameof(source));
            if (!source.IsReady)
                throw new ArgumentException("World text tracks require a layout-ready source target.", nameof(source));
            if (firstSeenTimestampMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(firstSeenTimestampMicroseconds));
            if (lastSeenTimestampMicroseconds < firstSeenTimestampMicroseconds)
                throw new ArgumentOutOfRangeException(nameof(lastSeenTimestampMicroseconds));
            if (observationCount <= 0) throw new ArgumentOutOfRangeException(nameof(observationCount));

            TrackId = trackId;
            Surface = surface;
            ObservedThisFrame = observedThisFrame;
            FirstSeenTimestampMicroseconds = firstSeenTimestampMicroseconds;
            LastSeenTimestampMicroseconds = lastSeenTimestampMicroseconds;
            ObservationCount = observationCount;
        }

        public long TrackId { get; }
        public WorldTextLayoutTarget Source { get; }
        public WorldTextSurface Surface { get; }
        public bool ObservedThisFrame { get; }
        public long FirstSeenTimestampMicroseconds { get; }
        public long LastSeenTimestampMicroseconds { get; }
        public int ObservationCount { get; }
    }

    public sealed class WorldTextTrackingPlan
    {
        public WorldTextTrackingPlan(long timestampMicroseconds, IReadOnlyList<WorldTextTrackState> tracks)
        {
            if (timestampMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(timestampMicroseconds));
            TimestampMicroseconds = timestampMicroseconds;
            Tracks = tracks ?? throw new ArgumentNullException(nameof(tracks));
        }

        public long TimestampMicroseconds { get; }
        public IReadOnlyList<WorldTextTrackState> Tracks { get; }
        public int ObservedCount => Tracks.Count(track => track.ObservedThisFrame);
        public int RetainedCount => Tracks.Count - ObservedCount;
    }

    /// <summary>
    /// Temporally stabilizes fitted world text surfaces. Association requires the same normalized source/display
    /// phrase and a nearby world-space center. Short observation gaps are retained, but stale tracks expire.
    /// No image/depth geometry is invented: only previously accepted WorldTextSurface values may enter a track.
    /// </summary>
    public sealed class WorldTextTrackStabilizer
    {
        private readonly double maximumAssociationDistanceMeters;
        private readonly long retentionMicroseconds;
        private readonly double smoothingTimeConstantSeconds;
        private readonly List<MutableTrack> tracks = new List<MutableTrack>();
        private long nextTrackId = 1;
        private long lastTimestampMicroseconds = -1;

        public WorldTextTrackStabilizer(
            double maximumAssociationDistanceMeters = 0.15,
            double retentionSeconds = 0.60,
            double smoothingTimeConstantSeconds = 0.12)
        {
            if (!IsFinitePositive(maximumAssociationDistanceMeters))
                throw new ArgumentOutOfRangeException(nameof(maximumAssociationDistanceMeters));
            if (!IsFinitePositive(retentionSeconds))
                throw new ArgumentOutOfRangeException(nameof(retentionSeconds));
            if (!IsFinitePositive(smoothingTimeConstantSeconds))
                throw new ArgumentOutOfRangeException(nameof(smoothingTimeConstantSeconds));

            this.maximumAssociationDistanceMeters = maximumAssociationDistanceMeters;
            retentionMicroseconds = checked((long)Math.Ceiling(retentionSeconds * 1_000_000.0));
            this.smoothingTimeConstantSeconds = smoothingTimeConstantSeconds;
        }

        public double MaximumAssociationDistanceMeters => maximumAssociationDistanceMeters;
        public long RetentionMicroseconds => retentionMicroseconds;
        public double SmoothingTimeConstantSeconds => smoothingTimeConstantSeconds;
        public int ActiveTrackCount => tracks.Count;

        public WorldTextTrackingPlan Update(WorldTextLayoutPlan layout, long timestampMicroseconds)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            if (timestampMicroseconds < 0) throw new ArgumentOutOfRangeException(nameof(timestampMicroseconds));
            if (lastTimestampMicroseconds >= 0 && timestampMicroseconds < lastTimestampMicroseconds)
            {
                throw new ArgumentException(
                    "World text tracking timestamps must be monotonic.",
                    nameof(timestampMicroseconds));
            }

            for (var index = 0; index < tracks.Count; index++)
                tracks[index].ObservedThisFrame = false;

            var claimedTrackIds = new HashSet<long>();
            foreach (var target in layout.Targets.Where(item => item.IsReady))
            {
                var key = BuildAssociationKey(target);
                var surface = target.Surface!.Value;
                var track = FindNearestTrack(key, surface.Center, claimedTrackIds);
                if (track == null)
                {
                    track = new MutableTrack(
                        nextTrackId++,
                        key,
                        target,
                        surface,
                        timestampMicroseconds);
                    tracks.Add(track);
                }
                else
                {
                    UpdateTrack(track, target, surface, timestampMicroseconds);
                }

                track.ObservedThisFrame = true;
                claimedTrackIds.Add(track.TrackId);
            }

            for (var index = tracks.Count - 1; index >= 0; index--)
            {
                if (timestampMicroseconds - tracks[index].LastSeenTimestampMicroseconds > retentionMicroseconds)
                    tracks.RemoveAt(index);
            }

            lastTimestampMicroseconds = timestampMicroseconds;
            var snapshots = tracks
                .OrderBy(track => track.TrackId)
                .Select(track => track.Snapshot())
                .ToArray();
            return new WorldTextTrackingPlan(timestampMicroseconds, snapshots);
        }

        public void Reset()
        {
            tracks.Clear();
            nextTrackId = 1;
            lastTimestampMicroseconds = -1;
        }

        private MutableTrack FindNearestTrack(
            string key,
            SpatialVector3 center,
            IReadOnlySet<long> claimedTrackIds)
        {
            MutableTrack best = null;
            var bestSquaredDistance = maximumAssociationDistanceMeters * maximumAssociationDistanceMeters;
            for (var index = 0; index < tracks.Count; index++)
            {
                var candidate = tracks[index];
                if (claimedTrackIds.Contains(candidate.TrackId)) continue;
                if (!string.Equals(candidate.AssociationKey, key, StringComparison.Ordinal)) continue;

                var delta = SpatialMath.Subtract(candidate.Surface.Center, center);
                var squaredDistance = delta.SquaredMagnitude;
                if (double.IsNaN(squaredDistance) || double.IsInfinity(squaredDistance)) continue;
                if (squaredDistance > bestSquaredDistance) continue;

                best = candidate;
                bestSquaredDistance = squaredDistance;
            }
            return best;
        }

        private void UpdateTrack(
            MutableTrack track,
            WorldTextLayoutTarget source,
            WorldTextSurface observation,
            long timestampMicroseconds)
        {
            var elapsedSeconds = Math.Max(
                0.0,
                (timestampMicroseconds - track.LastSeenTimestampMicroseconds) / 1_000_000.0);
            var alpha = elapsedSeconds <= 0.0
                ? 0.0
                : 1.0 - Math.Exp(-elapsedSeconds / smoothingTimeConstantSeconds);

            track.Source = source;
            track.Surface = BlendSurface(track.Surface, observation, alpha);
            track.LastSeenTimestampMicroseconds = timestampMicroseconds;
            track.ObservationCount++;
        }

        private static WorldTextSurface BlendSurface(
            WorldTextSurface previous,
            WorldTextSurface observed,
            double alpha)
        {
            var center = Lerp(previous.Center, observed.Center, alpha);
            var observedRight = AlignAxis(previous.Right, observed.Right);
            var observedUp = AlignAxis(previous.Up, observed.Up);

            var rightCandidate = Lerp(previous.Right, observedRight, alpha);
            if (!SpatialMath.TryNormalize(rightCandidate, out var right))
                right = previous.Right;

            var upCandidate = Lerp(previous.Up, observedUp, alpha);
            upCandidate = SpatialMath.Reject(upCandidate, right);
            if (!SpatialMath.TryNormalize(upCandidate, out var up))
                up = previous.Up;

            var normalCandidate = SpatialMath.Cross(right, up);
            if (!SpatialMath.TryNormalize(normalCandidate, out var normal))
                normal = previous.Normal;

            return new WorldTextSurface(
                center,
                right,
                up,
                normal,
                Lerp(previous.WidthMeters, observed.WidthMeters, alpha),
                Lerp(previous.HeightMeters, observed.HeightMeters, alpha),
                Math.Max(previous.MaxPlanarityErrorMeters, observed.MaxPlanarityErrorMeters));
        }

        private static SpatialVector3 AlignAxis(SpatialVector3 reference, SpatialVector3 candidate)
        {
            return SpatialMath.Dot(reference, candidate) < 0.0
                ? SpatialMath.Scale(candidate, -1.0)
                : candidate;
        }

        private static SpatialVector3 Lerp(SpatialVector3 from, SpatialVector3 to, double alpha)
        {
            return SpatialMath.Add(
                SpatialMath.Scale(from, 1.0 - alpha),
                SpatialMath.Scale(to, alpha));
        }

        private static double Lerp(double from, double to, double alpha)
        {
            return (from * (1.0 - alpha)) + (to * alpha);
        }

        private static string BuildAssociationKey(WorldTextLayoutTarget target)
        {
            var segment = target.Source.Source.Segment;
            return Normalize(segment.SourceText) + "\n" + Normalize(segment.DisplayText);
        }

        private static string Normalize(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var parts = text.Trim().ToLowerInvariant().Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts);
        }

        private static bool IsFinitePositive(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0;
        }

        private sealed class MutableTrack
        {
            public MutableTrack(
                long trackId,
                string associationKey,
                WorldTextLayoutTarget source,
                WorldTextSurface surface,
                long timestampMicroseconds)
            {
                TrackId = trackId;
                AssociationKey = associationKey;
                Source = source;
                Surface = surface;
                FirstSeenTimestampMicroseconds = timestampMicroseconds;
                LastSeenTimestampMicroseconds = timestampMicroseconds;
                ObservationCount = 1;
                ObservedThisFrame = true;
            }

            public long TrackId { get; }
            public string AssociationKey { get; }
            public WorldTextLayoutTarget Source { get; set; }
            public WorldTextSurface Surface { get; set; }
            public long FirstSeenTimestampMicroseconds { get; }
            public long LastSeenTimestampMicroseconds { get; set; }
            public int ObservationCount { get; set; }
            public bool ObservedThisFrame { get; set; }

            public WorldTextTrackState Snapshot()
            {
                return new WorldTextTrackState(
                    TrackId,
                    Source,
                    Surface,
                    ObservedThisFrame,
                    FirstSeenTimestampMicroseconds,
                    LastSeenTimestampMicroseconds,
                    ObservationCount);
            }
        }
    }
}
