using System;

namespace PhraseLayer.Core.Inputs
{
    /// <summary>
    /// Platform-neutral geometry contract mirroring PaddleOCR's get_rotate_crop_image.
    /// The source quad order is preserved: p0,p1,p2,p3 map to
    /// (0,0),(width,0),(width,height),(0,height) before an optional CCW 90-degree rotation.
    /// Image coordinates use top-left origin, +X right, +Y down.
    /// </summary>
    public readonly struct PaddleOcrCropRectificationPlan
    {
        internal PaddleOcrCropRectificationPlan(
            ImageQuad source,
            int warpWidth,
            int warpHeight,
            bool rotateCounterClockwise90)
        {
            Source = source;
            WarpWidth = warpWidth;
            WarpHeight = warpHeight;
            RotateCounterClockwise90 = rotateCounterClockwise90;
        }

        public ImageQuad Source { get; }

        /// <summary>
        /// Width passed to cv2.warpPerspective before optional rotation.
        /// PaddleOCR computes this as int(max(|p0-p1|, |p2-p3|)).
        /// </summary>
        public int WarpWidth { get; }

        /// <summary>
        /// Height passed to cv2.warpPerspective before optional rotation.
        /// PaddleOCR computes this as int(max(|p0-p3|, |p1-p2|)).
        /// </summary>
        public int WarpHeight { get; }

        /// <summary>
        /// PaddleOCR applies np.rot90 when WarpHeight / WarpWidth >= 1.5.
        /// np.rot90 without k rotates counter-clockwise by 90 degrees.
        /// </summary>
        public bool RotateCounterClockwise90 { get; }

        public int OutputWidth => RotateCounterClockwise90 ? WarpHeight : WarpWidth;
        public int OutputHeight => RotateCounterClockwise90 ? WarpWidth : WarpHeight;

        /// <summary>
        /// Destination points used by PaddleOCR before optional rotation.
        /// Note that PaddleOCR uses width/height, not width-1/height-1, in getPerspectiveTransform.
        /// </summary>
        public ImageQuad WarpDestination => new ImageQuad(
            new ImagePoint(0.0, 0.0),
            new ImagePoint(WarpWidth, 0.0),
            new ImagePoint(WarpWidth, WarpHeight),
            new ImagePoint(0.0, WarpHeight));
    }

    public static class PaddleOcrCropRectification
    {
        public const double RotateAspectRatioThreshold = 1.5;

        public static PaddleOcrCropRectificationPlan CreatePlan(ImageQuad source)
        {
            ValidateFinite(source.P0, nameof(source));
            ValidateFinite(source.P1, nameof(source));
            ValidateFinite(source.P2, nameof(source));
            ValidateFinite(source.P3, nameof(source));

            var topWidth = Distance(source.P0, source.P1);
            var bottomWidth = Distance(source.P2, source.P3);
            var leftHeight = Distance(source.P0, source.P3);
            var rightHeight = Distance(source.P1, source.P2);

            // Python int() truncates positive distances toward zero; C# cast has the same behavior.
            var warpWidth = (int)Math.Max(topWidth, bottomWidth);
            var warpHeight = (int)Math.Max(leftHeight, rightHeight);

            if (warpWidth <= 0)
                throw new ArgumentException("PaddleOCR crop width becomes zero after integer truncation.", nameof(source));
            if (warpHeight <= 0)
                throw new ArgumentException("PaddleOCR crop height becomes zero after integer truncation.", nameof(source));

            var rotateCounterClockwise90 =
                ((double)warpHeight / warpWidth) >= RotateAspectRatioThreshold;

            return new PaddleOcrCropRectificationPlan(
                source,
                warpWidth,
                warpHeight,
                rotateCounterClockwise90);
        }

        private static double Distance(ImagePoint a, ImagePoint b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static void ValidateFinite(ImagePoint point, string parameterName)
        {
            if (double.IsNaN(point.X) || double.IsInfinity(point.X) ||
                double.IsNaN(point.Y) || double.IsInfinity(point.Y))
            {
                throw new ArgumentException("Crop quad coordinates must be finite.", parameterName);
            }
        }
    }
}
