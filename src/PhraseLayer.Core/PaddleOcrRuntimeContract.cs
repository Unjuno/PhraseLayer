using System;

namespace PhraseLayer.Core.Inputs
{
    public sealed class PaddleDetectorRuntimeContract
    {
        internal PaddleDetectorRuntimeContract(int[] outputShape, int mapWidth, int mapHeight, int valueCount)
        {
            OutputShape = outputShape;
            MapWidth = mapWidth;
            MapHeight = mapHeight;
            ValueCount = valueCount;
        }

        public int[] OutputShape { get; }
        public int MapWidth { get; }
        public int MapHeight { get; }
        public int ValueCount { get; }

        public override string ToString()
        {
            return "detector shape=" + PaddleOcrRuntimeContract.FormatShape(OutputShape) +
                   " map=" + MapWidth + "x" + MapHeight +
                   " values=" + ValueCount;
        }
    }

    public sealed class PaddleRecognizerRuntimeContract
    {
        internal PaddleRecognizerRuntimeContract(
            int[] outputShape,
            int timeSteps,
            int classCount,
            int dictionaryTokenCount,
            int valueCount)
        {
            OutputShape = outputShape;
            TimeSteps = timeSteps;
            ClassCount = classCount;
            DictionaryTokenCount = dictionaryTokenCount;
            ValueCount = valueCount;
        }

        public int[] OutputShape { get; }
        public int TimeSteps { get; }
        public int ClassCount { get; }
        public int DictionaryTokenCount { get; }
        public int ValueCount { get; }

        public override string ToString()
        {
            return "recognizer shape=" + PaddleOcrRuntimeContract.FormatShape(OutputShape) +
                   " time=" + TimeSteps +
                   " classes=" + ClassCount +
                   " dictionary=" + DictionaryTokenCount +
                   " values=" + ValueCount;
        }
    }

    /// <summary>
    /// Runtime contracts that cannot be proven from ONNX import metadata alone.
    /// These validators are deliberately platform-neutral so the same shape/class rules are covered by host CI.
    /// </summary>
    public static class PaddleOcrRuntimeContract
    {
        public static PaddleDetectorRuntimeContract ValidateDetector(
            int[] outputShape,
            float[] outputValues)
        {
            if (outputShape == null) throw new ArgumentNullException(nameof(outputShape));
            if (outputValues == null) throw new ArgumentNullException(nameof(outputValues));

            var map = PaddleDbProbabilityMap.FromTensor(outputShape, outputValues);
            return new PaddleDetectorRuntimeContract(
                SnapshotShape(outputShape),
                map.Width,
                map.Height,
                outputValues.Length);
        }

        public static PaddleRecognizerRuntimeContract ValidateRecognizer(
            int[] outputShape,
            float[] outputValues,
            int dictionaryTokenCount)
        {
            if (outputShape == null) throw new ArgumentNullException(nameof(outputShape));
            if (outputValues == null) throw new ArgumentNullException(nameof(outputValues));
            if (dictionaryTokenCount < 0) throw new ArgumentOutOfRangeException(nameof(dictionaryTokenCount));

            if (outputShape.Length != 3 || outputShape[0] != 1)
            {
                throw new InvalidOperationException(
                    "Recognizer output must be [1,time,class]. Observed " + FormatShape(outputShape) + ".");
            }

            var timeSteps = outputShape[1];
            var classCount = outputShape[2];
            if (timeSteps <= 0 || classCount <= 0)
            {
                throw new InvalidOperationException(
                    "Recognizer time/class dimensions must be positive. Observed " + FormatShape(outputShape) + ".");
            }

            var expectedValues = checked(timeSteps * classCount);
            if (outputValues.Length != expectedValues)
            {
                throw new InvalidOperationException(
                    "Recognizer value count does not match timeSteps * classCount. Observed shape " +
                    FormatShape(outputShape) + ", values=" + outputValues.Length + ".");
            }

            var expectedClassCount = checked(dictionaryTokenCount + 1);
            if (classCount != expectedClassCount)
            {
                throw new InvalidOperationException(
                    "Recognizer class count must equal dictionary token count + 1 CTC blank. " +
                    "Observed classes=" + classCount + ", dictionary=" + dictionaryTokenCount +
                    ", expected classes=" + expectedClassCount + ".");
            }

            return new PaddleRecognizerRuntimeContract(
                SnapshotShape(outputShape),
                timeSteps,
                classCount,
                dictionaryTokenCount,
                outputValues.Length);
        }

        public static string BuildReport(
            PaddleDetectorRuntimeContract? detector,
            PaddleRecognizerRuntimeContract? recognizer,
            int dictionaryTokenCount)
        {
            if (dictionaryTokenCount < 0) throw new ArgumentOutOfRangeException(nameof(dictionaryTokenCount));

            var detectorText = detector == null ? "detector=unobserved" : detector.ToString();
            var recognizerText = recognizer == null ? "recognizer=unobserved" : recognizer.ToString();
            return detectorText + "; " + recognizerText + "; configured_dictionary=" + dictionaryTokenCount;
        }

        public static string FormatShape(int[] shape)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            return "[" + string.Join(",", shape) + "]";
        }

        private static int[] SnapshotShape(int[] shape)
        {
            var snapshot = new int[shape.Length];
            Array.Copy(shape, snapshot, shape.Length);
            return snapshot;
        }
    }
}
