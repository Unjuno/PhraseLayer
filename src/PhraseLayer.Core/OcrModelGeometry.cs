using System;

namespace PhraseLayer.Core.Inputs
{
    /// <summary>
    /// A point in OCR model-input pixel coordinates. Origin is top-left; +X is right and +Y is down.
    /// Keeping this separate from ImagePoint prevents accidental mixing of model-space and source-image coordinates.
    /// </summary>
    public readonly struct OcrModelPoint
    {
        public OcrModelPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    public readonly struct OcrModelQuad
    {
        public OcrModelQuad(OcrModelPoint p0, OcrModelPoint p1, OcrModelPoint p2, OcrModelPoint p3)
        {
            P0 = p0;
            P1 = p1;
            P2 = p2;
            P3 = p3;
        }

        public OcrModelPoint P0 { get; }
        public OcrModelPoint P1 { get; }
        public OcrModelPoint P2 { get; }
        public OcrModelPoint P3 { get; }
    }

    public interface IOcrModelCoordinateTransform
    {
        OcrModelPoint SourceToModel(ImagePoint sourcePoint);
        ImagePoint ModelToSource(OcrModelPoint modelPoint);
        OcrModelQuad SourceToModel(ImageQuad sourceQuad);
        ImageQuad ModelToSource(OcrModelQuad modelQuad);
    }

    /// <summary>
    /// Geometry for aspect-preserving resize plus centered letterbox padding.
    /// This remains available for OCR models that actually use letterboxing; PP-OCR uses a separate transform.
    /// </summary>
    public sealed class OcrLetterboxTransform : IOcrModelCoordinateTransform
    {
        private OcrLetterboxTransform(
            int sourceWidth,
            int sourceHeight,
            int modelWidth,
            int modelHeight,
            double scale,
            double paddingX,
            double paddingY)
        {
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            ModelWidth = modelWidth;
            ModelHeight = modelHeight;
            Scale = scale;
            PaddingX = paddingX;
            PaddingY = paddingY;
        }

        public int SourceWidth { get; }
        public int SourceHeight { get; }
        public int ModelWidth { get; }
        public int ModelHeight { get; }
        public double Scale { get; }
        public double PaddingX { get; }
        public double PaddingY { get; }
        public double ResizedWidth => SourceWidth * Scale;
        public double ResizedHeight => SourceHeight * Scale;

        public static OcrLetterboxTransform Create(
            int sourceWidth,
            int sourceHeight,
            int modelWidth,
            int modelHeight)
        {
            ValidateDimension(sourceWidth, nameof(sourceWidth));
            ValidateDimension(sourceHeight, nameof(sourceHeight));
            ValidateDimension(modelWidth, nameof(modelWidth));
            ValidateDimension(modelHeight, nameof(modelHeight));

            var scale = Math.Min(
                modelWidth / (double)sourceWidth,
                modelHeight / (double)sourceHeight);
            var resizedWidth = sourceWidth * scale;
            var resizedHeight = sourceHeight * scale;

            return new OcrLetterboxTransform(
                sourceWidth,
                sourceHeight,
                modelWidth,
                modelHeight,
                scale,
                (modelWidth - resizedWidth) / 2.0,
                (modelHeight - resizedHeight) / 2.0);
        }

        public OcrModelPoint SourceToModel(ImagePoint sourcePoint)
        {
            return new OcrModelPoint(
                sourcePoint.X * Scale + PaddingX,
                sourcePoint.Y * Scale + PaddingY);
        }

        public ImagePoint ModelToSource(OcrModelPoint modelPoint)
        {
            return new ImagePoint(
                (modelPoint.X - PaddingX) / Scale,
                (modelPoint.Y - PaddingY) / Scale);
        }

        public ImagePoint NormalizedModelToSource(double u, double v)
        {
            return ModelToSource(new OcrModelPoint(u * ModelWidth, v * ModelHeight));
        }

        public OcrModelQuad SourceToModel(ImageQuad sourceQuad)
        {
            return new OcrModelQuad(
                SourceToModel(sourceQuad.P0),
                SourceToModel(sourceQuad.P1),
                SourceToModel(sourceQuad.P2),
                SourceToModel(sourceQuad.P3));
        }

        public ImageQuad ModelToSource(OcrModelQuad modelQuad)
        {
            return new ImageQuad(
                ModelToSource(modelQuad.P0),
                ModelToSource(modelQuad.P1),
                ModelToSource(modelQuad.P2),
                ModelToSource(modelQuad.P3));
        }

        private static void ValidateDimension(int value, string parameterName)
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    public sealed class OcrDetectionCandidate
    {
        public OcrDetectionCandidate(OcrModelQuad modelBounds, double confidence)
        {
            ValidateConfidence(confidence, nameof(confidence));
            ModelBounds = modelBounds;
            Confidence = confidence;
        }

        public OcrModelQuad ModelBounds { get; }
        public double Confidence { get; }

        private static void ValidateConfidence(double confidence, string parameterName)
        {
            if (double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence < 0.0 || confidence > 1.0)
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    public sealed class OcrRecognitionCandidate
    {
        public OcrRecognitionCandidate(string text, double confidence)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            if (double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence < 0.0 || confidence > 1.0)
                throw new ArgumentOutOfRangeException(nameof(confidence));
            Confidence = confidence;
        }

        public string Text { get; }
        public double Confidence { get; }
    }

    public static class OcrModelOutputMapper
    {
        /// <summary>
        /// Converts detector model-space geometry plus recognizer output into the source-image OcrRegion contract.
        /// The minimum of detector and recognizer scores is used as a conservative quality score; it is not a calibrated probability.
        /// </summary>
        public static OcrRegion ToSourceRegion(
            OcrDetectionCandidate detection,
            OcrRecognitionCandidate recognition,
            IOcrModelCoordinateTransform transform)
        {
            if (detection == null) throw new ArgumentNullException(nameof(detection));
            if (recognition == null) throw new ArgumentNullException(nameof(recognition));
            if (transform == null) throw new ArgumentNullException(nameof(transform));

            return new OcrRegion(
                recognition.Text,
                Math.Min(detection.Confidence, recognition.Confidence),
                transform.ModelToSource(detection.ModelBounds));
        }
    }
}
