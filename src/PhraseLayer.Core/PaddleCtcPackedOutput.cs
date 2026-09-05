using System;

namespace PhraseLayer.Core.Inputs
{
    /// <summary>
    /// CPU boundary for the GPU CTC ABI: float32 [1,time,2], interleaved class index then maximum score.
    /// All values and dimensions are dimensionless. No Unity types or tensor implementation are required.
    /// </summary>
    public sealed class PaddleCtcPackedOutput
    {
        public const int ValuesPerTimestep = 2;
        // Conservative exclusive bound for class indices. Every accepted index is exactly representable in float32.
        public const int MaximumClassCount = 16777216;

        private PaddleCtcPackedOutput(int[] outputShape, int[] classIndices, float[] maxScores)
        {
            OutputShape = (int[])outputShape.Clone();
            ClassIndices = classIndices;
            MaxScores = maxScores;
        }

        public int[] OutputShape { get; }
        public int[] ClassIndices { get; }
        public float[] MaxScores { get; }

        /// <summary>
        /// Checks the actual packed tensor shape, not just its flattened element count. In particular [1,2,time]
        /// is not interchangeable with [1,time,2]. Call before downloading a runtime tensor as well as when unpacking.
        /// </summary>
        public static void ValidateShapes(int[] probabilityShape, int[] packedShape)
        {
            if (probabilityShape == null) throw new ArgumentNullException(nameof(probabilityShape));
            if (packedShape == null) throw new ArgumentNullException(nameof(packedShape));
            if (probabilityShape.Length != 3 || probabilityShape[0] != 1 ||
                probabilityShape[1] <= 0 || probabilityShape[2] <= 0)
                throw new InvalidOperationException("Recognizer probability shape witness must be [1,time,class] with positive dimensions.");
            if (probabilityShape[2] > MaximumClassCount)
                throw new InvalidOperationException("Recognizer class count exceeds the reviewed float32 exact-integer packing limit.");
            if (packedShape.Length != 3 || packedShape[0] != 1 ||
                packedShape[1] != probabilityShape[1] || packedShape[2] != ValuesPerTimestep)
                throw new InvalidOperationException("Packed recognizer tensor must be [1,time,2] with the same timestep count as its probability witness.");
        }

        /// <summary>
        /// Rejects invalid winners even at blank or duplicate timesteps, before CTC filtering can hide them.
        /// Checking winning scores in [0,1] does not prove normalization of the unobserved full probability matrix.
        /// </summary>
        public static PaddleCtcPackedOutput Unpack(int[] probabilityShape, int[] packedShape, float[] packedValues)
        {
            if (packedValues == null) throw new ArgumentNullException(nameof(packedValues));
            ValidateShapes(probabilityShape, packedShape);
            var timeSteps = probabilityShape[1];
            var classCount = probabilityShape[2];
            var expectedValues = checked(timeSteps * ValuesPerTimestep);
            if (packedValues.Length != expectedValues)
                throw new InvalidOperationException("Packed recognizer buffer must contain exactly two values per timestep.");

            // Validate all values before allocating or casting. The class-count limit makes the float comparison
            // exact and rules out the Int32.MaxValue-to-float rounding hazard at 2147483648.
            for (var time = 0; time < timeSteps; time++)
            {
                var classValue = packedValues[time * ValuesPerTimestep];
                var score = packedValues[time * ValuesPerTimestep + 1];
                if (float.IsNaN(classValue) || float.IsInfinity(classValue) ||
                    classValue < 0f || classValue >= classCount || classValue != Math.Truncate((double)classValue))
                    throw new InvalidOperationException("Packed recognizer class index must be an exact integer in the witnessed class range.");
                if (float.IsNaN(score) || float.IsInfinity(score) || score < 0f || score > 1f)
                    throw new InvalidOperationException("Packed recognizer maximum score must be finite and in [0,1], including blank/duplicate timesteps.");
            }

            var classIndices = new int[timeSteps];
            var maxScores = new float[timeSteps];
            for (var time = 0; time < timeSteps; time++)
            {
                classIndices[time] = (int)packedValues[time * ValuesPerTimestep];
                maxScores[time] = packedValues[time * ValuesPerTimestep + 1];
            }
            return new PaddleCtcPackedOutput(probabilityShape, classIndices, maxScores);
        }
    }
}
