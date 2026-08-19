using System;

namespace PhraseLayer.Core.Inputs
{
    public enum PaddleDbScoreMode
    {
        Fast = 0,
        Slow = 1
    }

    public readonly struct DbBitmapPoint
    {
        public DbBitmapPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    public readonly struct DbDestinationPoint
    {
        public DbDestinationPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    /// <summary>
    /// Platform-neutral contract for PP-OCRv6 tiny DB detector postprocessing.
    /// Contour extraction, minimum-area rectangles, polygon filling, and polygon offsetting
    /// remain backend responsibilities because PaddleOCR implements them with OpenCV/pyclipper.
    /// </summary>
    public sealed class PaddleDbPostprocessSpec
    {
        public const double V6TinyBitmapThreshold = 0.2;
        public const double V6TinyBoxThreshold = 0.4;
        public const int V6TinyMaxCandidates = 3000;
        public const double V6TinyUnclipRatio = 1.4;
        public const double DefaultMinimumShortSide = 3.0;

        public PaddleDbPostprocessSpec(
            double bitmapThreshold,
            double boxThreshold,
            int maxCandidates,
            double unclipRatio,
            PaddleDbScoreMode scoreMode = PaddleDbScoreMode.Fast,
            double minimumShortSide = DefaultMinimumShortSide)
        {
            ValidateUnitInterval(bitmapThreshold, nameof(bitmapThreshold));
            ValidateUnitInterval(boxThreshold, nameof(boxThreshold));
            if (maxCandidates <= 0) throw new ArgumentOutOfRangeException(nameof(maxCandidates));
            if (double.IsNaN(unclipRatio) || double.IsInfinity(unclipRatio) || unclipRatio <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(unclipRatio));
            if (!Enum.IsDefined(typeof(PaddleDbScoreMode), scoreMode))
                throw new ArgumentOutOfRangeException(nameof(scoreMode));
            if (double.IsNaN(minimumShortSide) || double.IsInfinity(minimumShortSide) || minimumShortSide <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(minimumShortSide));

            BitmapThreshold = bitmapThreshold;
            BoxThreshold = boxThreshold;
            MaxCandidates = maxCandidates;
            UnclipRatio = unclipRatio;
            ScoreMode = scoreMode;
            MinimumShortSide = minimumShortSide;
        }

        public double BitmapThreshold { get; }
        public double BoxThreshold { get; }
        public int MaxCandidates { get; }
        public double UnclipRatio { get; }
        public PaddleDbScoreMode ScoreMode { get; }
        public double MinimumShortSide { get; }
        public double MinimumShortSideAfterUnclip => MinimumShortSide + 2.0;

        public static PaddleDbPostprocessSpec V6Tiny()
        {
            return new PaddleDbPostprocessSpec(
                V6TinyBitmapThreshold,
                V6TinyBoxThreshold,
                V6TinyMaxCandidates,
                V6TinyUnclipRatio,
                PaddleDbScoreMode.Fast,
                DefaultMinimumShortSide);
        }

        /// <summary>
        /// Mirrors PaddleOCR: segmentation = prediction > thresh.
        /// Equality with the bitmap threshold is background.
        /// </summary>
        public bool IsForeground(double prediction)
        {
            ValidateUnitInterval(prediction, nameof(prediction));
            return prediction > BitmapThreshold;
        }

        /// <summary>
        /// Mirrors PaddleOCR: candidates are rejected only when box_thresh > score.
        /// Equality with the box threshold is accepted.
        /// </summary>
        public bool AcceptBoxScore(double score)
        {
            ValidateUnitInterval(score, nameof(score));
            return score >= BoxThreshold;
        }

        public bool AcceptShortSide(double shortSide, bool afterUnclip)
        {
            if (double.IsNaN(shortSide) || double.IsInfinity(shortSide) || shortSide < 0.0)
                throw new ArgumentOutOfRangeException(nameof(shortSide));
            var required = afterUnclip ? MinimumShortSideAfterUnclip : MinimumShortSide;
            return shortSide >= required;
        }

        /// <summary>
        /// Distance used by PaddleOCR's polygon unclip operation: area * unclipRatio / perimeter.
        /// The actual polygon offset is intentionally delegated to a geometry backend.
        /// </summary>
        public double ComputeUnclipDistance(double polygonArea, double polygonPerimeter)
        {
            if (double.IsNaN(polygonArea) || double.IsInfinity(polygonArea) || polygonArea < 0.0)
                throw new ArgumentOutOfRangeException(nameof(polygonArea));
            if (double.IsNaN(polygonPerimeter) || double.IsInfinity(polygonPerimeter) || polygonPerimeter <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(polygonPerimeter));
            return polygonArea * UnclipRatio / polygonPerimeter;
        }

        /// <summary>
        /// Mirrors PaddleOCR's final bitmap-to-destination coordinate scaling and clipping.
        /// NumPy round uses ties-to-even semantics, matched here explicitly.
        /// </summary>
        public static DbDestinationPoint ScaleBitmapPoint(
            DbBitmapPoint point,
            int bitmapWidth,
            int bitmapHeight,
            int destinationWidth,
            int destinationHeight)
        {
            ValidatePositive(bitmapWidth, nameof(bitmapWidth));
            ValidatePositive(bitmapHeight, nameof(bitmapHeight));
            ValidatePositive(destinationWidth, nameof(destinationWidth));
            ValidatePositive(destinationHeight, nameof(destinationHeight));

            var x = Math.Round(point.X / bitmapWidth * destinationWidth, MidpointRounding.ToEven);
            var y = Math.Round(point.Y / bitmapHeight * destinationHeight, MidpointRounding.ToEven);
            return new DbDestinationPoint(
                Clamp(x, 0.0, destinationWidth),
                Clamp(y, 0.0, destinationHeight));
        }

        private static void ValidateUnitInterval(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(parameterName);
        }

        private static void ValidatePositive(int value, string parameterName)
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(parameterName);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }
    }
}
