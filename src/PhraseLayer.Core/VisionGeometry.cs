using System;
using System.Collections.Generic;
using System.Linq;

namespace PhraseLayer.Core.Inputs
{
    /// <summary>
    /// A point in OCR/image pixel coordinates. Origin is the image top-left; +X is right and +Y is down.
    /// Values may extend slightly outside the image because detectors can overshoot frame edges.
    /// </summary>
    public readonly struct ImagePoint
    {
        public ImagePoint(double x, double y) { X = x; Y = y; }
        public double X { get; }
        public double Y { get; }
    }

    /// <summary>
    /// Four OCR polygon corners in source-image coordinates. Corner order is preserved from the detector.
    /// </summary>
    public readonly struct ImageQuad
    {
        public ImageQuad(ImagePoint p0, ImagePoint p1, ImagePoint p2, ImagePoint p3)
        { P0 = p0; P1 = p1; P2 = p2; P3 = p3; }

        public ImagePoint P0 { get; }
        public ImagePoint P1 { get; }
        public ImagePoint P2 { get; }
        public ImagePoint P3 { get; }

        public IReadOnlyList<ImagePoint> Points => new[] { P0, P1, P2, P3 };

        public static ImageQuad FromRect(double x, double y, double width, double height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            return new ImageQuad(
                new ImagePoint(x, y),
                new ImagePoint(x + width, y),
                new ImagePoint(x + width, y + height),
                new ImagePoint(x, y + height));
        }
    }

    /// <summary>
    /// A normalized viewport point. Origin is bottom-left; U and V are clamped to [0,1].
    /// This matches the coordinate convention expected by viewport-to-ray APIs.
    /// </summary>
    public readonly struct ViewportPoint
    {
        public ViewportPoint(double u, double v)
        {
            U = Clamp01(u);
            V = Clamp01(v);
        }

        public double U { get; }
        public double V { get; }

        private static double Clamp01(double value) => Math.Max(0.0, Math.Min(1.0, value));
    }

    public readonly struct ViewportQuad
    {
        public ViewportQuad(ViewportPoint p0, ViewportPoint p1, ViewportPoint p2, ViewportPoint p3)
        { P0 = p0; P1 = p1; P2 = p2; P3 = p3; }

        public ViewportPoint P0 { get; }
        public ViewportPoint P1 { get; }
        public ViewportPoint P2 { get; }
        public ViewportPoint P3 { get; }

        public IReadOnlyList<ViewportPoint> Points => new[] { P0, P1, P2, P3 };
        public ViewportPoint Centroid => new ViewportPoint(
            (P0.U + P1.U + P2.U + P3.U) / 4.0,
            (P0.V + P1.V + P2.V + P3.V) / 4.0);
    }

    public static class ImageCoordinateMapper
    {
        /// <summary>
        /// Converts OCR top-left image coordinates into bottom-left normalized viewport coordinates.
        /// Values outside the frame are clamped so detector overshoot cannot produce invalid viewport input.
        /// </summary>
        public static ViewportPoint ToViewport(ImagePoint point, int imageWidth, int imageHeight)
        {
            ValidateDimensions(imageWidth, imageHeight);
            return new ViewportPoint(point.X / imageWidth, 1.0 - (point.Y / imageHeight));
        }

        public static ViewportQuad ToViewport(ImageQuad quad, int imageWidth, int imageHeight)
        {
            ValidateDimensions(imageWidth, imageHeight);
            return new ViewportQuad(
                ToViewport(quad.P0, imageWidth, imageHeight),
                ToViewport(quad.P1, imageWidth, imageHeight),
                ToViewport(quad.P2, imageWidth, imageHeight),
                ToViewport(quad.P3, imageWidth, imageHeight));
        }

        private static void ValidateDimensions(int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        }
    }

    public sealed class OcrRegion
    {
        public OcrRegion(string text, double confidence, ImageQuad imageBounds)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            if (confidence < 0.0 || confidence > 1.0) throw new ArgumentOutOfRangeException(nameof(confidence));
            Confidence = confidence;
            ImageBounds = imageBounds;
        }

        public string Text { get; }
        public double Confidence { get; }
        public ImageQuad ImageBounds { get; }
    }

    public sealed class OcrViewportRegion
    {
        public OcrViewportRegion(OcrRegion source, ViewportQuad viewportBounds)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            ViewportBounds = viewportBounds;
        }

        public OcrRegion Source { get; }
        public ViewportQuad ViewportBounds { get; }
        public ViewportPoint Anchor => ViewportBounds.Centroid;
    }

    public static class OcrViewportMapper
    {
        public static IReadOnlyList<OcrViewportRegion> Map(OcrObservation observation, ImageFrame frame)
        {
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            if (frame == null) throw new ArgumentNullException(nameof(frame));

            return observation.Regions
                .Select(region => new OcrViewportRegion(
                    region,
                    ImageCoordinateMapper.ToViewport(region.ImageBounds, frame.Width, frame.Height)))
                .ToArray();
        }
    }
}
