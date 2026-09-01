using System;

namespace PhraseLayer.Core.Spatial
{
    public enum WorldTextMaskSuppressionReason
    {
        None = 0,
        NotObservedThisFrame = 1,
        InsufficientObservations = 2,
        NoVisibleReplacement = 3,
        ExcessivePlanarityError = 4
    }

    public readonly struct WorldTextMaskDecision
    {
        public WorldTextMaskDecision(bool canMask, WorldTextMaskSuppressionReason suppressionReason)
        {
            if (canMask && suppressionReason != WorldTextMaskSuppressionReason.None)
                throw new ArgumentException("A maskable track cannot also have a suppression reason.", nameof(suppressionReason));
            if (!canMask && suppressionReason == WorldTextMaskSuppressionReason.None)
                throw new ArgumentException("A suppressed track must provide a suppression reason.", nameof(suppressionReason));

            CanMask = canMask;
            SuppressionReason = suppressionReason;
        }

        public bool CanMask { get; }
        public WorldTextMaskSuppressionReason SuppressionReason { get; }

        public static WorldTextMaskDecision Allowed() =>
            new WorldTextMaskDecision(true, WorldTextMaskSuppressionReason.None);

        public static WorldTextMaskDecision Suppressed(WorldTextMaskSuppressionReason reason) =>
            new WorldTextMaskDecision(false, reason);
    }

    /// <summary>
    /// Conservative policy for covering the physical source text before drawing a translated replacement.
    ///
    /// Text tracks may be retained briefly to reduce flicker, but masks are never retained through an observation gap:
    /// hiding the wrong physical region is more harmful than briefly showing the source text. A track must also be seen
    /// repeatedly and satisfy a stricter planarity threshold than the general world-text layout gate.
    /// </summary>
    public sealed class WorldTextMaskPolicy
    {
        public WorldTextMaskPolicy(
            int minimumObservationCount = 2,
            double maximumPlanarityErrorMeters = 0.01)
        {
            if (minimumObservationCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(minimumObservationCount));
            if (maximumPlanarityErrorMeters < 0.0 ||
                double.IsNaN(maximumPlanarityErrorMeters) ||
                double.IsInfinity(maximumPlanarityErrorMeters))
            {
                throw new ArgumentOutOfRangeException(nameof(maximumPlanarityErrorMeters));
            }

            MinimumObservationCount = minimumObservationCount;
            MaximumPlanarityErrorMeters = maximumPlanarityErrorMeters;
        }

        public int MinimumObservationCount { get; }
        public double MaximumPlanarityErrorMeters { get; }

        public WorldTextMaskDecision Evaluate(WorldTextTrackState track)
        {
            if (track == null) throw new ArgumentNullException(nameof(track));

            if (!track.ObservedThisFrame)
                return WorldTextMaskDecision.Suppressed(WorldTextMaskSuppressionReason.NotObservedThisFrame);
            if (track.ObservationCount < MinimumObservationCount)
                return WorldTextMaskDecision.Suppressed(WorldTextMaskSuppressionReason.InsufficientObservations);

            var segment = track.Source.Source.Source.Segment;
            if (string.IsNullOrWhiteSpace(segment.DisplayText) ||
                string.Equals(segment.SourceText, segment.DisplayText, StringComparison.Ordinal))
            {
                return WorldTextMaskDecision.Suppressed(WorldTextMaskSuppressionReason.NoVisibleReplacement);
            }

            if (track.Surface.MaxPlanarityErrorMeters > MaximumPlanarityErrorMeters)
                return WorldTextMaskDecision.Suppressed(WorldTextMaskSuppressionReason.ExcessivePlanarityError);

            return WorldTextMaskDecision.Allowed();
        }
    }
}
