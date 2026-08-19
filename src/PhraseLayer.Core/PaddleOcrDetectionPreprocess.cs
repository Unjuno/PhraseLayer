using System;

namespace PhraseLayer.Core.Inputs
{
    public enum PaddleDetLimitType
    {
        Min = 0,
        Max = 1,
        ResizeLong = 2
    }

    public readonly struct PaddleNormalizedBgrPixel
    {
        public PaddleNormalizedBgrPixel(float channel0, float channel1, float channel2)
        {
            Channel0 = channel0;
            Channel1 = channel1;
            Channel2 = channel2;
        }

        public float Channel0 { get; }
        public float Channel1 { get; }
        public float Channel2 { get; }
    }

    /// <summary>
    /// Reproduces PaddleOCR DetResizeForTest geometry for model-space/source-space mapping.
    /// Unlike a letterbox transform, width and height are resized independently after stride rounding,
    /// so RatioWidth and RatioHeight can differ slightly.
    /// </summary>
    public sealed class PaddleDetResizeTransform : IOcrModelCoordinateTransform
    {
        internal PaddleDetResizeTransform(
            int sourceWidth,
            int sourceHeight,
            int paddedWidth,
            int paddedHeight,
            int modelWidth,
            int modelHeight)
        {
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            PaddedWidth = paddedWidth;
            PaddedHeight = paddedHeight;
            ModelWidth = modelWidth;
            ModelHeight = modelHeight;
            RatioWidth = modelWidth / (double)paddedWidth;
            RatioHeight = modelHeight / (double)paddedHeight;
        }

        public int SourceWidth { get; }
        public int SourceHeight { get; }
        public int PaddedWidth { get; }
        public int PaddedHeight { get; }
        public int ModelWidth { get; }
        public int ModelHeight { get; }
        public double RatioWidth { get; }
        public double RatioHeight { get; }
        public bool UsesSmallImagePadding => PaddedWidth != SourceWidth || PaddedHeight != SourceHeight;

        public OcrModelPoint SourceToModel(ImagePoint sourcePoint)
        {
            return new OcrModelPoint(
                sourcePoint.X * RatioWidth,
                sourcePoint.Y * RatioHeight);
        }

        public ImagePoint ModelToSource(OcrModelPoint modelPoint)
        {
            return new ImagePoint(
                modelPoint.X / RatioWidth,
                modelPoint.Y / RatioHeight);
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
    }

    /// <summary>
    /// PP-OCRv6 tiny detector preprocessing contract mirrored from PaddleOCR's official
    /// inference.yml plus DetResizeForTest implementation.
    /// Input pixels are decoded in BGR order, normalized in HWC, then transposed to CHW.
    /// </summary>
    public static class PaddleOcrV6TinyDetectionPreprocess
    {
        public const int DefaultLimitSideLength = 736;
        public const int DefaultMaxSideLimit = 4000;
        public const int NetworkStride = 32;
        public const int SmallImagePaddingThreshold = 64;
        public const float PixelScale = 1.0f / 255.0f;

        private static readonly float[] Means = { 0.485f, 0.456f, 0.406f };
        private static readonly float[] StandardDeviations = { 0.229f, 0.224f, 0.225f };

        public static PaddleDetResizeTransform CreateResizeTransform(int sourceWidth, int sourceHeight)
        {
            return CreateResizeTransform(
                sourceWidth,
                sourceHeight,
                DefaultLimitSideLength,
                PaddleDetLimitType.Min,
                DefaultMaxSideLimit,
                NetworkStride);
        }

        public static PaddleDetResizeTransform CreateResizeTransform(
            int sourceWidth,
            int sourceHeight,
            int limitSideLength,
            PaddleDetLimitType limitType,
            int maxSideLimit,
            int stride)
        {
            ValidatePositive(sourceWidth, nameof(sourceWidth));
            ValidatePositive(sourceHeight, nameof(sourceHeight));
            ValidatePositive(limitSideLength, nameof(limitSideLength));
            ValidatePositive(maxSideLimit, nameof(maxSideLimit));
            ValidatePositive(stride, nameof(stride));

            var paddedWidth = sourceWidth;
            var paddedHeight = sourceHeight;
            if ((long)sourceWidth + sourceHeight < SmallImagePaddingThreshold)
            {
                paddedWidth = Math.Max(NetworkStride, sourceWidth);
                paddedHeight = Math.Max(NetworkStride, sourceHeight);
            }

            var ratio = InitialResizeRatio(paddedWidth, paddedHeight, limitSideLength, limitType);
            var resizedWidth = (int)(paddedWidth * ratio);
            var resizedHeight = (int)(paddedHeight * ratio);

            var resizedMaxSide = Math.Max(resizedWidth, resizedHeight);
            if (resizedMaxSide > maxSideLimit)
            {
                var maxSideRatio = maxSideLimit / (double)resizedMaxSide;
                resizedWidth = (int)(resizedWidth * maxSideRatio);
                resizedHeight = (int)(resizedHeight * maxSideRatio);
            }

            resizedWidth = RoundToStride(resizedWidth, stride);
            resizedHeight = RoundToStride(resizedHeight, stride);

            return new PaddleDetResizeTransform(
                sourceWidth,
                sourceHeight,
                paddedWidth,
                paddedHeight,
                resizedWidth,
                resizedHeight);
        }

        public static PaddleNormalizedBgrPixel NormalizeBgr(byte blue, byte green, byte red)
        {
            return new PaddleNormalizedBgrPixel(
                NormalizeChannel(blue, 0),
                NormalizeChannel(green, 1),
                NormalizeChannel(red, 2));
        }

        public static float NormalizeChannel(byte value, int channelIndex)
        {
            if (channelIndex < 0 || channelIndex >= Means.Length)
                throw new ArgumentOutOfRangeException(nameof(channelIndex));

            return (value * PixelScale - Means[channelIndex]) / StandardDeviations[channelIndex];
        }

        public static float MeanForChannel(int channelIndex)
        {
            if (channelIndex < 0 || channelIndex >= Means.Length)
                throw new ArgumentOutOfRangeException(nameof(channelIndex));
            return Means[channelIndex];
        }

        public static float StandardDeviationForChannel(int channelIndex)
        {
            if (channelIndex < 0 || channelIndex >= StandardDeviations.Length)
                throw new ArgumentOutOfRangeException(nameof(channelIndex));
            return StandardDeviations[channelIndex];
        }

        private static double InitialResizeRatio(
            int width,
            int height,
            int limitSideLength,
            PaddleDetLimitType limitType)
        {
            switch (limitType)
            {
                case PaddleDetLimitType.Max:
                    var maxSide = Math.Max(width, height);
                    return maxSide > limitSideLength
                        ? limitSideLength / (double)maxSide
                        : 1.0;

                case PaddleDetLimitType.Min:
                    var minSide = Math.Min(width, height);
                    return minSide < limitSideLength
                        ? limitSideLength / (double)minSide
                        : 1.0;

                case PaddleDetLimitType.ResizeLong:
                    return limitSideLength / (double)Math.Max(width, height);

                default:
                    throw new ArgumentOutOfRangeException(nameof(limitType));
            }
        }

        private static int RoundToStride(int value, int stride)
        {
            var strideUnits = value / (double)stride;
            var roundedUnits = (int)Math.Round(strideUnits, MidpointRounding.ToEven);
            return Math.Max(roundedUnits * stride, stride);
        }

        private static void ValidatePositive(int value, string parameterName)
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
