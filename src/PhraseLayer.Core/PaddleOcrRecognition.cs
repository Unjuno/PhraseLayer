using System;
using System.Collections.Generic;
using System.Text;

namespace PhraseLayer.Core.Inputs
{
    /// <summary>
    /// Geometry for PaddleOCR recognition preprocessing.
    /// The source crop is resized to a fixed height while preserving aspect ratio, capped at ModelWidth,
    /// and then zero-padded on the right to ModelWidth.
    /// </summary>
    public sealed class PaddleRecResizeTransform
    {
        internal PaddleRecResizeTransform(
            int sourceWidth,
            int sourceHeight,
            int modelWidth,
            int modelHeight,
            int resizedWidth)
        {
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
            ModelWidth = modelWidth;
            ModelHeight = modelHeight;
            ResizedWidth = resizedWidth;
        }

        public int SourceWidth { get; }
        public int SourceHeight { get; }
        public int ModelWidth { get; }
        public int ModelHeight { get; }
        public int ResizedWidth { get; }
        public int RightPaddingWidth => ModelWidth - ResizedWidth;
        public double RatioWidth => ResizedWidth / (double)SourceWidth;
        public double RatioHeight => ModelHeight / (double)SourceHeight;
        public double ValidRatio => Math.Min(1.0, ResizedWidth / (double)ModelWidth);
        public bool IsWidthCapped => (long)ModelHeight * SourceWidth > (long)ModelWidth * SourceHeight;
    }

    /// <summary>
    /// PP-OCRv6 recognition preprocessing contract mirrored from PaddleOCR's recognition inference path.
    /// The default model shape is BGR CHW [3, 48, 320]. Pixels are scaled to [0,1], shifted by 0.5,
    /// divided by 0.5, and unused columns are zero-padded on the right.
    ///
    /// Dynamic-width ONNX inputs can pass a wider modelWidth explicitly. The pinned ONNX must still be
    /// probed in real Unity before selecting a production width/batching policy.
    /// </summary>
    public static class PaddleOcrV6TinyRecognitionPreprocess
    {
        public const int Channels = 3;
        public const int DefaultModelHeight = 48;
        public const int DefaultModelWidth = 320;
        public const float PixelScale = 1.0f / 255.0f;
        public const float Mean = 0.5f;
        public const float StandardDeviation = 0.5f;

        public static PaddleRecResizeTransform CreateResizeTransform(
            int sourceWidth,
            int sourceHeight,
            int modelWidth = DefaultModelWidth,
            int modelHeight = DefaultModelHeight)
        {
            ValidatePositive(sourceWidth, nameof(sourceWidth));
            ValidatePositive(sourceHeight, nameof(sourceHeight));
            ValidatePositive(modelWidth, nameof(modelWidth));
            ValidatePositive(modelHeight, nameof(modelHeight));

            var scaledWidth = modelHeight * (sourceWidth / (double)sourceHeight);
            var resizedWidth = (int)Math.Ceiling(scaledWidth);
            if (resizedWidth > modelWidth)
                resizedWidth = modelWidth;
            if (resizedWidth < 1)
                resizedWidth = 1;

            return new PaddleRecResizeTransform(
                sourceWidth,
                sourceHeight,
                modelWidth,
                modelHeight,
                resizedWidth);
        }

        public static float NormalizeChannel(byte value)
        {
            return (value * PixelScale - Mean) / StandardDeviation;
        }

        private static void ValidatePositive(int value, string parameterName)
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    /// <summary>
    /// Result of PaddleOCR-compatible CTC greedy decoding.
    /// Confidence mirrors PaddleOCR: the arithmetic mean of the per-timestep maximum values that survive
    /// duplicate removal and blank filtering. It is only a probability when the model output itself is probabilistic.
    /// </summary>
    public sealed class PaddleCtcDecodeResult
    {
        public PaddleCtcDecodeResult(string text, double confidence, int emittedTokenCount)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
            if (double.IsNaN(confidence) || double.IsInfinity(confidence))
                throw new ArgumentOutOfRangeException(nameof(confidence));
            if (emittedTokenCount < 0)
                throw new ArgumentOutOfRangeException(nameof(emittedTokenCount));

            Confidence = confidence;
            EmittedTokenCount = emittedTokenCount;
        }

        public string Text { get; }
        public double Confidence { get; }
        public int EmittedTokenCount { get; }
    }

    /// <summary>
    /// Platform-neutral mirror of PaddleOCR CTCLabelDecode.
    /// The external dictionary excludes the CTC blank token; blank is inserted conceptually at class index 0,
    /// so dictionary token i maps to model class i+1.
    /// </summary>
    public static class PaddleCtcGreedyDecoder
    {
        public const int BlankIndex = 0;

        public static PaddleCtcDecodeResult DecodeFromPredictions(
            float[] predictions,
            int timeSteps,
            int classCount,
            IReadOnlyList<string> characterDictionary)
        {
            if (predictions == null) throw new ArgumentNullException(nameof(predictions));
            ValidateDictionary(characterDictionary);
            if (timeSteps < 0) throw new ArgumentOutOfRangeException(nameof(timeSteps));
            if (classCount <= 0) throw new ArgumentOutOfRangeException(nameof(classCount));
            if (classCount != characterDictionary.Count + 1)
            {
                throw new ArgumentException(
                    "CTC class count must equal dictionary count + 1 blank class.",
                    nameof(classCount));
            }

            var expectedLength = checked(timeSteps * classCount);
            if (predictions.Length != expectedLength)
            {
                throw new ArgumentException(
                    "Prediction buffer length must equal timeSteps * classCount.",
                    nameof(predictions));
            }

            var indices = new int[timeSteps];
            var scores = new float[timeSteps];
            for (var time = 0; time < timeSteps; time++)
            {
                var offset = time * classCount;
                var bestClass = 0;
                var bestValue = predictions[offset];
                ValidateFinite(bestValue, nameof(predictions));

                // NumPy argmax returns the first index on ties, so only a strict improvement replaces the winner.
                for (var classIndex = 1; classIndex < classCount; classIndex++)
                {
                    var value = predictions[offset + classIndex];
                    ValidateFinite(value, nameof(predictions));
                    if (value > bestValue)
                    {
                        bestValue = value;
                        bestClass = classIndex;
                    }
                }

                indices[time] = bestClass;
                scores[time] = bestValue;
            }

            return DecodeFromIndices(indices, scores, characterDictionary);
        }

        public static PaddleCtcDecodeResult DecodeFromIndices(
            IReadOnlyList<int> classIndices,
            IReadOnlyList<float> maxScores,
            IReadOnlyList<string> characterDictionary)
        {
            if (classIndices == null) throw new ArgumentNullException(nameof(classIndices));
            if (maxScores == null) throw new ArgumentNullException(nameof(maxScores));
            ValidateDictionary(characterDictionary);
            if (classIndices.Count != maxScores.Count)
                throw new ArgumentException("classIndices and maxScores must have the same timestep count.");

            var builder = new StringBuilder();
            double confidenceSum = 0.0;
            var emittedCount = 0;

            for (var time = 0; time < classIndices.Count; time++)
            {
                var classIndex = classIndices[time];
                var score = maxScores[time];
                ValidateFinite(score, nameof(maxScores));

                if (classIndex < 0 || classIndex > characterDictionary.Count)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(classIndices),
                        "CTC class index is outside [0, dictionary.Count].");
                }

                // PaddleOCR removes consecutive duplicates before filtering ignored blank tokens.
                if (time > 0 && classIndex == classIndices[time - 1])
                    continue;
                if (classIndex == BlankIndex)
                    continue;

                var token = characterDictionary[classIndex - 1];
                builder.Append(token);
                confidenceSum += score;
                emittedCount++;
            }

            var confidence = emittedCount == 0 ? 0.0 : confidenceSum / emittedCount;
            return new PaddleCtcDecodeResult(builder.ToString(), confidence, emittedCount);
        }

        private static void ValidateDictionary(IReadOnlyList<string> characterDictionary)
        {
            if (characterDictionary == null) throw new ArgumentNullException(nameof(characterDictionary));
            for (var index = 0; index < characterDictionary.Count; index++)
            {
                if (characterDictionary[index] == null)
                {
                    throw new ArgumentException(
                        "Character dictionary cannot contain null tokens.",
                        nameof(characterDictionary));
                }
            }
        }

        private static void ValidateFinite(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
