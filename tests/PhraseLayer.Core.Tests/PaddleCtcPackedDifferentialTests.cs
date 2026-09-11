using System;
using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class PaddleCtcPackedDifferentialTests
    {
        private const int RandomizedTrials = 1000;
        private const int Seed = 0x5A17C0DE;

        [Fact]
        public void PackedAbiMatchesFullPredictionDecoderAcrossDeterministicRandomMatrices()
        {
            var random = new Random(Seed);

            for (var trial = 0; trial < RandomizedTrials; trial++)
            {
                var classCount = random.Next(2, 18);
                var timeSteps = random.Next(1, 33);
                var dictionary = CreateDictionary(classCount - 1);
                var predictions = new float[checked(timeSteps * classCount)];
                var packed = new float[checked(timeSteps * PaddleCtcPackedOutput.ValuesPerTimestep)];

                for (var time = 0; time < timeSteps; time++)
                {
                    FillProbabilityRow(random, predictions, time, classCount, forceMaximumTie: time % 3 == 0);
                    ReduceFirstMaximum(
                        predictions,
                        time,
                        classCount,
                        out var expectedClass,
                        out var expectedScore);

                    var packedOffset = time * PaddleCtcPackedOutput.ValuesPerTimestep;
                    packed[packedOffset] = expectedClass;
                    packed[packedOffset + 1] = expectedScore;
                }

                var probabilityShape = new[] { 1, timeSteps, classCount };
                var packedShape = new[] { 1, timeSteps, PaddleCtcPackedOutput.ValuesPerTimestep };
                var unpacked = PaddleCtcPackedOutput.Unpack(probabilityShape, packedShape, packed);
                var fullDecoded = PaddleCtcGreedyDecoder.DecodeFromPredictions(
                    predictions,
                    timeSteps,
                    classCount,
                    dictionary);
                var packedDecoded = PaddleCtcGreedyDecoder.DecodeFromIndices(
                    unpacked.ClassIndices,
                    unpacked.MaxScores,
                    dictionary);

                Assert.Equal(fullDecoded.Text, packedDecoded.Text);
                Assert.Equal(fullDecoded.EmittedTokenCount, packedDecoded.EmittedTokenCount);
                Assert.Equal(fullDecoded.Confidence, packedDecoded.Confidence, 12);

                for (var time = 0; time < timeSteps; time++)
                {
                    ReduceFirstMaximum(
                        predictions,
                        time,
                        classCount,
                        out var expectedClass,
                        out var expectedScore);
                    Assert.Equal(expectedClass, unpacked.ClassIndices[time]);
                    Assert.Equal(expectedScore, unpacked.MaxScores[time]);
                }
            }
        }

        [Fact]
        public void PackedAbiMatchesFullDecoderAtPinnedPpOcrDictionaryScale()
        {
            var classCount = PaddleOcrDictionaryManifestContract.ExpectedEffectiveTokenCount + 1;
            var dictionary = CreateDictionary(classCount - 1);
            const int timeSteps = 6;
            var predictions = new float[checked(timeSteps * classCount)];

            // blank, highest dictionary class, a first-index tie, repeated class, blank, then repeated class again.
            SetOneHot(predictions, 0, classCount, 0, 1f);
            SetOneHot(predictions, 1, classCount, classCount - 1, 1f);
            predictions[2 * classCount + 2] = .5f;
            predictions[2 * classCount + classCount - 1] = .5f;
            SetOneHot(predictions, 3, classCount, 2, 1f);
            SetOneHot(predictions, 4, classCount, 0, 1f);
            SetOneHot(predictions, 5, classCount, 2, 1f);

            var packed = PackCpuOracle(predictions, timeSteps, classCount);
            var unpacked = PaddleCtcPackedOutput.Unpack(
                new[] { 1, timeSteps, classCount },
                new[] { 1, timeSteps, PaddleCtcPackedOutput.ValuesPerTimestep },
                packed);

            var fullDecoded = PaddleCtcGreedyDecoder.DecodeFromPredictions(
                predictions,
                timeSteps,
                classCount,
                dictionary);
            var packedDecoded = PaddleCtcGreedyDecoder.DecodeFromIndices(
                unpacked.ClassIndices,
                unpacked.MaxScores,
                dictionary);

            Assert.Equal(new[] { 0, classCount - 1, 2, 2, 0, 2 }, unpacked.ClassIndices);
            Assert.Equal(fullDecoded.Text, packedDecoded.Text);
            Assert.Equal(fullDecoded.EmittedTokenCount, packedDecoded.EmittedTokenCount);
            Assert.Equal(fullDecoded.Confidence, packedDecoded.Confidence, 12);
        }

        private static string[] CreateDictionary(int count)
        {
            var dictionary = new string[count];
            for (var index = 0; index < count; index++)
                dictionary[index] = "<" + index + ">";
            return dictionary;
        }

        private static void FillProbabilityRow(
            Random random,
            float[] predictions,
            int time,
            int classCount,
            bool forceMaximumTie)
        {
            var weights = new int[classCount];
            var sum = 0;
            for (var classIndex = 0; classIndex < classCount; classIndex++)
            {
                var weight = random.Next(0, 9);
                weights[classIndex] = weight;
                sum += weight;
            }

            if (forceMaximumTie && classCount >= 2)
            {
                var first = random.Next(0, classCount);
                var second = random.Next(0, classCount - 1);
                if (second >= first)
                    second++;

                sum -= weights[first];
                sum -= weights[second];
                weights[first] = 9;
                weights[second] = 9;
                sum += 18;
            }

            if (sum == 0)
            {
                weights[0] = 1;
                sum = 1;
            }

            var offset = time * classCount;
            for (var classIndex = 0; classIndex < classCount; classIndex++)
                predictions[offset + classIndex] = weights[classIndex] / (float)sum;
        }

        private static float[] PackCpuOracle(float[] predictions, int timeSteps, int classCount)
        {
            var packed = new float[checked(timeSteps * PaddleCtcPackedOutput.ValuesPerTimestep)];
            for (var time = 0; time < timeSteps; time++)
            {
                ReduceFirstMaximum(predictions, time, classCount, out var bestClass, out var bestScore);
                var packedOffset = time * PaddleCtcPackedOutput.ValuesPerTimestep;
                packed[packedOffset] = bestClass;
                packed[packedOffset + 1] = bestScore;
            }
            return packed;
        }

        private static void ReduceFirstMaximum(
            float[] predictions,
            int time,
            int classCount,
            out int bestClass,
            out float bestScore)
        {
            var offset = checked(time * classCount);
            bestClass = 0;
            bestScore = predictions[offset];
            for (var classIndex = 1; classIndex < classCount; classIndex++)
            {
                var score = predictions[offset + classIndex];
                // NumPy/Paddle and reviewed Unity ArgMax both keep the first index on an exact tie.
                if (score > bestScore)
                {
                    bestScore = score;
                    bestClass = classIndex;
                }
            }
        }

        private static void SetOneHot(
            float[] predictions,
            int time,
            int classCount,
            int classIndex,
            float score)
        {
            predictions[checked(time * classCount + classIndex)] = score;
        }
    }
}
