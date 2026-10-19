using System;
using PhraseLayer.Core.Inputs;
using UnityEditor;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Host-only real-Unity parity: injected probability tensors exercise the production packing operators, including
    /// ties and the highest pinned dictionary index. Three image widths compare real-model full versus packed output.
    /// Compile-shell success is not execution evidence. No Quest or camera is used by this probe.
    /// </summary>
    public static class PhraseLayerPaddleOcrRecognizerGpuReductionProbe
    {
        private const string RecognizerPath = "Assets/LocalOcrAssets/PaddleOCR/recognizer.onnx";
        private const string DictionaryPath = "Assets/LocalOcrAssets/PaddleOCR/ppocr_keys.txt";
        private const int SourceHeight = 48;
        private const float ScoreTolerance = 1e-6f;

        [MenuItem("PhraseLayer/PP-OCR/Run Recognizer GPU Reduction Parity Probe")]
        public static void Run()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            RunSyntheticPackingProbe();
            AssetDatabase.Refresh();
            var recognizer = LoadRequired<ModelAsset>(RecognizerPath);
            var dictionaryAsset = LoadRequired<TextAsset>(DictionaryPath);
            var dictionary = PaddleOcrCharacterDictionary.Parse(dictionaryAsset.text, useSpaceCharacter: true);
            if (dictionary.Count != PaddleOcrDictionaryManifestContract.ExpectedEffectiveTokenCount)
                throw new InvalidOperationException("Recognizer GPU reduction parity requires the exact pinned dictionary token count.");

            using (var runtime = new UnityPaddleOcrRecognizerRuntime(recognizer, BackendType.GPUCompute))
            {
                if (!runtime.UsesGpuCtcReduction || runtime.RetainsFullOutputWorker || runtime.CtcReadbackOperationsPerCrop != 1)
                    throw new InvalidOperationException("Recognizer must use the GPU reduced-only single-readback path.");

                foreach (var width in new[] { 64, 192, 321 })
                {
                    var texture = CreatePatternTexture(width);
                    try
                    {
                        var full = runtime.Execute(texture, PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth);
                        var fullContract = PaddleOcrRuntimeContract.ValidateRecognizer(full.OutputShape, full.OutputValues, dictionary.Count);
                        ValidateProbabilityMatrix(full.OutputValues, fullContract.TimeSteps, fullContract.ClassCount);
                        var reduced = runtime.ExecuteReduced(texture, PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth);
                        var reducedContract = PaddleOcrRuntimeContract.ValidateRecognizerReduced(
                            reduced.OutputShape, reduced.ClassIndices, reduced.MaxScores, dictionary.Count);
                        RequireSameShape(full.OutputShape, reduced.OutputShape);
                        if (fullContract.TimeSteps != reducedContract.TimeSteps || fullContract.ClassCount != reducedContract.ClassCount)
                            throw new InvalidOperationException("Recognizer GPU packing changed the time/class contract.");

                        var expectedIndices = new int[fullContract.TimeSteps];
                        var expectedScores = new float[fullContract.TimeSteps];
                        ReduceOnCpu(full.OutputValues, fullContract.TimeSteps, fullContract.ClassCount, expectedIndices, expectedScores);
                        RequireReducedParity(expectedIndices, expectedScores, reduced);
                        var fullDecoded = full.Decode(dictionary);
                        var reducedDecoded = reduced.Decode(dictionary);
                        if (!string.Equals(fullDecoded.Text, reducedDecoded.Text, StringComparison.Ordinal))
                            throw new InvalidOperationException("Recognizer GPU packing changed decoded CTC text.");
                        if (fullDecoded.EmittedTokenCount != reducedDecoded.EmittedTokenCount)
                            throw new InvalidOperationException("Recognizer GPU packing changed emitted CTC token count.");
                        if (Math.Abs(fullDecoded.Confidence - reducedDecoded.Confidence) > ScoreTolerance)
                            throw new InvalidOperationException("Recognizer GPU packing changed decoded CTC confidence.");
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                    }
                }
            }
            Debug.Log("PhraseLayer PP-OCR recognizer GPU reduction parity PASS; model_fixtures=3 synthetic_fixtures=2 " +
                "packed_layout=BTC2 class_indices=exact scores_tolerance=0.000001 readbacks_per_crop=1 " +
                "decoded_text=exact emitted_tokens=exact confidence=parity quest_executed=false");
#else
            throw new InvalidOperationException("Resolve the reviewed com.unity.ai.inference 2.2.1 package before running GPU reduction parity.");
#endif
        }

        public static void RunBatch()
        {
            try { Run(); EditorApplication.Exit(0); }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

        /// <summary>Real GPU operator parity without downloading any model or using a camera.</summary>
        public static void RunSyntheticBatch()
        {
            try
            {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
                RunSyntheticPackingProbe();
                Debug.Log("PhraseLayer PP-OCR synthetic GPU packing parity PASS; fixtures=2 quest_executed=false");
                EditorApplication.Exit(0);
#else
                throw new InvalidOperationException("Reviewed Inference Engine 2.2.1 compile gate is required.");
#endif
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        private static void RunSyntheticPackingProbe()
        {
            var values = new[]
            {
                .2f, .2f, .2f, .2f, .2f,
                0f, .1f, .45f, .45f, 0f,
                0f, .1f, .8f, .1f, 0f,
                .6f, .1f, .1f, .1f, .1f,
                .1f, .1f, .6f, .1f, .1f,
                .05f, .05f, .05f, .05f, .8f,
                .05f, .05f, .05f, .05f, .8f,
                .2f, .2f, .2f, .2f, .2f
            };
            var packed = RunSyntheticCase(8, 5, values, new[] { 0, 2, 2, 0, 2, 4, 4, 0 });
            var decoded = PaddleCtcGreedyDecoder.DecodeFromIndices(packed.ClassIndices, packed.MaxScores, new[] { "a", "b", "c", "d" });
            if (decoded.Text != "bbd" || decoded.EmittedTokenCount != 3 ||
                Math.Abs(decoded.Confidence - ((double).45f + .6f + .8f) / 3) > ScoreTolerance)
                throw new InvalidOperationException("GPU packing changed ties, blank-separated duplicates or CTC confidence.");

            var classes = PaddleOcrDictionaryManifestContract.ExpectedEffectiveTokenCount + 1;
            var large = new float[2 * classes];
            large[classes - 1] = 1f;
            large[classes + 1] = .5f;
            large[2 * classes - 1] = .5f;
            RunSyntheticCase(2, classes, large, new[] { classes - 1, 1 });
        }

        private static PaddleCtcPackedOutput RunSyntheticCase(int timeSteps, int classCount, float[] values, int[] expectedIndices)
        {
            var shape = new TensorShape(1, timeSteps, classCount);
            var graph = new FunctionalGraph();
            // An input, not Functional.Constant: constant folding must not replace the GPU packing operators.
            var input = graph.AddInput(DataType.Float, shape);
            var packed = UnityPaddleOcrRecognizerRuntime.PackReviewedCtcOutput(input);
            graph.AddOutputs(packed);
            using (var worker = new Worker(graph.Compile(), BackendType.GPUCompute))
            using (var tensor = new Tensor<float>(shape, values))
            {
                worker.Schedule(tensor);
                var output = worker.PeekOutput() as Tensor<float>;
                if (output == null) throw new InvalidOperationException("Synthetic packing output must be float32.");
                using (var cpu = output.ReadbackAndClone())
                {
                    var result = PaddleCtcPackedOutput.Unpack(new[] { 1, timeSteps, classCount }, CopyShape(cpu.shape), cpu.DownloadToArray());
                    var cpuIndices = new int[timeSteps];
                    var cpuScores = new float[timeSteps];
                    ReduceOnCpu(values, timeSteps, classCount, cpuIndices, cpuScores);
                    for (var time = 0; time < timeSteps; time++)
                    {
                        if (cpuIndices[time] != expectedIndices[time] || result.ClassIndices[time] != expectedIndices[time] ||
                            Math.Abs(result.MaxScores[time] - cpuScores[time]) > ScoreTolerance)
                            throw new InvalidOperationException("Synthetic GPU packed index/score parity failed at timestep " + time + ".");
                    }
                    return result;
                }
            }
        }

        private static Texture2D CreatePatternTexture(int width)
        {
            var texture = new Texture2D(width, SourceHeight, TextureFormat.RGBA32, false, false)
            {
                name = "PhraseLayer PP-OCR GPU Packing Fixture",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color32[width * SourceHeight];
            for (var y = 0; y < SourceHeight; y++)
                for (var x = 0; x < width; x++)
                    pixels[y * width + x] = new Color32((byte)((x * 13 + y * 7 + 31) & 255),
                        (byte)((x * 3 + y * 17 + 59) & 255), (byte)((x * 11 + y * 5 + 101) & 255), 255);
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static void ValidateProbabilityMatrix(float[] values, int timeSteps, int classCount)
        {
            if (values == null || values.Length != checked(timeSteps * classCount))
                throw new InvalidOperationException("Recognizer full probability matrix has an invalid length.");
            for (var time = 0; time < timeSteps; time++)
            {
                double sum = 0;
                for (var cls = 0; cls < classCount; cls++)
                {
                    var value = values[time * classCount + cls];
                    if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f)
                        throw new InvalidOperationException("Recognizer full output must be finite and in [0,1].");
                    sum += value;
                }
                if (Math.Abs(sum - 1.0) > .001)
                    throw new InvalidOperationException("Recognizer full probability rows must sum to one within 0.001.");
            }
        }

        private static void ReduceOnCpu(float[] probabilities, int timeSteps, int classCount, int[] indices, float[] scores)
        {
            for (var time = 0; time < timeSteps; time++)
            {
                var offset = time * classCount;
                var bestIndex = 0;
                var bestScore = probabilities[offset];
                for (var classIndex = 1; classIndex < classCount; classIndex++)
                {
                    var score = probabilities[offset + classIndex];
                    // Strict improvement preserves the first class on a tie.
                    if (score > bestScore) { bestScore = score; bestIndex = classIndex; }
                }
                indices[time] = bestIndex;
                scores[time] = bestScore;
            }
        }

        private static void RequireReducedParity(int[] expectedIndices, float[] expectedScores, PaddleRecognizerReducedOutput actual)
        {
            if (actual.ClassIndices.Length != expectedIndices.Length || actual.MaxScores.Length != expectedScores.Length)
                throw new InvalidOperationException("Packed recognizer timestep count differs from the CPU oracle.");
            for (var time = 0; time < expectedIndices.Length; time++)
            {
                if (actual.ClassIndices[time] != expectedIndices[time])
                    throw new InvalidOperationException("GPU ArgMax parity failed at timestep " + time + ".");
                var error = Math.Abs(actual.MaxScores[time] - expectedScores[time]);
                if (float.IsNaN(error) || float.IsInfinity(error) || error > ScoreTolerance)
                    throw new InvalidOperationException("GPU ReduceMax parity failed at timestep " + time + ".");
            }
        }

        private static void RequireSameShape(int[] expected, int[] actual)
        {
            if (expected.Length != actual.Length) throw new InvalidOperationException("Recognizer output rank changed.");
            for (var axis = 0; axis < expected.Length; axis++)
                if (expected[axis] != actual[axis]) throw new InvalidOperationException("Recognizer output shape changed.");
        }

        private static int[] CopyShape(TensorShape shape)
        {
            var result = new int[shape.rank];
            for (var axis = 0; axis < result.Length; axis++) result[axis] = shape[axis];
            return result;
        }

        private static T LoadRequired<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) throw new InvalidOperationException("Required GPU parity asset is missing: " + path);
            return asset;
        }
#endif
    }
}
