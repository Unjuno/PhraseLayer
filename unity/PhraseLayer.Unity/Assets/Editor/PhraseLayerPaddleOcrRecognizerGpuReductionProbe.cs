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
    /// Real-Unity parity gate for the production recognizer CTC GPU reduction path.
    ///
    /// The full [1,time,class] output remains the correctness oracle. The same input is then executed through the
    /// wrapped ArgMax + ReduceMax model and every timestep's class index and maximum score must match the CPU greedy
    /// reduction. Only after this gate passes may live OCR use the reduced readback path.
    /// </summary>
    public static class PhraseLayerPaddleOcrRecognizerGpuReductionProbe
    {
        private const string RecognizerPath = "Assets/LocalOcrAssets/PaddleOCR/recognizer.onnx";
        private const string DictionaryPath = "Assets/LocalOcrAssets/PaddleOCR/ppocr_keys.txt";
        private const int SourceWidth = 192;
        private const int SourceHeight = 48;
        private const float ScoreTolerance = 1e-6f;

        [MenuItem("PhraseLayer/PP-OCR/Run Recognizer GPU Reduction Parity Probe")]
        public static void Run()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            AssetDatabase.Refresh();
            var recognizer = LoadRequired<ModelAsset>(RecognizerPath);
            var dictionaryAsset = LoadRequired<TextAsset>(DictionaryPath);
            var dictionary = PaddleOcrCharacterDictionary.Parse(dictionaryAsset.text, useSpaceCharacter: true);
            if (dictionary.Count <= 0)
                throw new InvalidOperationException("Recognizer GPU reduction parity requires a non-empty character dictionary.");

            var texture = CreatePatternTexture();
            try
            {
                using (var runtime = new UnityPaddleOcrRecognizerRuntime(recognizer, BackendType.GPUCompute))
                {
                    if (!runtime.UsesGpuCtcReduction)
                        throw new InvalidOperationException("Production recognizer runtime did not enable GPU CTC reduction.");

                    var full = runtime.Execute(texture, PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth);
                    var fullContract = PaddleOcrRuntimeContract.ValidateRecognizer(
                        full.OutputShape,
                        full.OutputValues,
                        dictionary.Count);
                    ValidateProbabilityMatrix(full.OutputValues);

                    var reduced = runtime.ExecuteReduced(texture, PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth);
                    var reducedContract = PaddleOcrRuntimeContract.ValidateRecognizerReduced(
                        reduced.OutputShape,
                        reduced.ClassIndices,
                        reduced.MaxScores,
                        dictionary.Count);

                    RequireSameShape(full.OutputShape, reduced.OutputShape);
                    if (fullContract.TimeSteps != reducedContract.TimeSteps ||
                        fullContract.ClassCount != reducedContract.ClassCount)
                    {
                        throw new InvalidOperationException("Recognizer GPU reduction changed the reviewed time/class contract.");
                    }

                    var expectedIndices = new int[fullContract.TimeSteps];
                    var expectedScores = new float[fullContract.TimeSteps];
                    ReduceOnCpu(
                        full.OutputValues,
                        fullContract.TimeSteps,
                        fullContract.ClassCount,
                        expectedIndices,
                        expectedScores);
                    RequireReducedParity(expectedIndices, expectedScores, reduced);

                    var fullDecoded = full.Decode(dictionary);
                    var reducedDecoded = reduced.Decode(dictionary);
                    if (!string.Equals(fullDecoded.Text, reducedDecoded.Text, StringComparison.Ordinal))
                        throw new InvalidOperationException("Recognizer GPU reduction changed decoded CTC text.");
                    if (fullDecoded.EmittedTokenCount != reducedDecoded.EmittedTokenCount)
                        throw new InvalidOperationException("Recognizer GPU reduction changed emitted CTC token count.");
                    if (Math.Abs(fullDecoded.Confidence - reducedDecoded.Confidence) > ScoreTolerance)
                        throw new InvalidOperationException("Recognizer GPU reduction changed decoded CTC confidence.");

                    Debug.Log(
                        "PhraseLayer PP-OCR recognizer GPU reduction parity PASS; " +
                        "time=" + fullContract.TimeSteps +
                        " classes=" + fullContract.ClassCount +
                        " full_cpu_values=" + full.OutputValues.Length +
                        " reduced_cpu_values=" + (reduced.ClassIndices.Length + reduced.MaxScores.Length) +
                        " indices=exact scores_tolerance=" + ScoreTolerance +
                        " decoded_text=exact emitted_tokens=exact confidence=parity");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve com.unity.ai.inference 2.2.1 before running recognizer GPU reduction parity.");
#endif
        }

        public static void RunBatch()
        {
            try
            {
                Run();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        private static Texture2D CreatePatternTexture()
        {
            var texture = new Texture2D(SourceWidth, SourceHeight, TextureFormat.RGBA32, false, false)
            {
                name = "PhraseLayer PP-OCR Recognizer GPU Reduction Fixture",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[SourceWidth * SourceHeight];
            for (var y = 0; y < SourceHeight; y++)
            {
                for (var x = 0; x < SourceWidth; x++)
                {
                    pixels[y * SourceWidth + x] = new Color32(
                        (byte)((x * 13 + y * 7 + 31) & 0xff),
                        (byte)((x * 3 + y * 17 + 59) & 0xff),
                        (byte)((x * 11 + y * 5 + 101) & 0xff),
                        255);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static void ValidateProbabilityMatrix(float[] values)
        {
            if (values == null || values.Length == 0)
                throw new InvalidOperationException("Recognizer full probability matrix is empty.");
            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f)
                {
                    throw new InvalidOperationException(
                        "Recognizer full output must remain a finite probability matrix in [0,1]; index=" +
                        index + " value=" + value + ".");
                }
            }
        }

        private static void ReduceOnCpu(
            float[] probabilities,
            int timeSteps,
            int classCount,
            int[] indices,
            float[] scores)
        {
            for (var time = 0; time < timeSteps; time++)
            {
                var offset = time * classCount;
                var bestIndex = 0;
                var bestScore = probabilities[offset];
                for (var classIndex = 1; classIndex < classCount; classIndex++)
                {
                    var score = probabilities[offset + classIndex];
                    // NumPy/Paddle greedy argmax keeps the first index on a tie.
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = classIndex;
                    }
                }
                indices[time] = bestIndex;
                scores[time] = bestScore;
            }
        }

        private static void RequireReducedParity(
            int[] expectedIndices,
            float[] expectedScores,
            PaddleRecognizerReducedOutput actual)
        {
            if (actual.ClassIndices.Length != expectedIndices.Length || actual.MaxScores.Length != expectedScores.Length)
                throw new InvalidOperationException("Recognizer GPU reduction timestep count drifted from CPU oracle.");

            for (var time = 0; time < expectedIndices.Length; time++)
            {
                if (actual.ClassIndices[time] != expectedIndices[time])
                {
                    throw new InvalidOperationException(
                        "Recognizer GPU ArgMax parity failed at timestep " + time +
                        ": expected=" + expectedIndices[time] + " actual=" + actual.ClassIndices[time] + ".");
                }
                var error = Math.Abs(actual.MaxScores[time] - expectedScores[time]);
                if (error > ScoreTolerance)
                {
                    throw new InvalidOperationException(
                        "Recognizer GPU ReduceMax parity failed at timestep " + time +
                        ": expected=" + expectedScores[time] + " actual=" + actual.MaxScores[time] +
                        " error=" + error + ".");
                }
            }
        }

        private static void RequireSameShape(int[] expected, int[] actual)
        {
            if (expected == null || actual == null || expected.Length != actual.Length)
                throw new InvalidOperationException("Recognizer GPU reduction changed output rank.");
            for (var axis = 0; axis < expected.Length; axis++)
            {
                if (expected[axis] != actual[axis])
                    throw new InvalidOperationException("Recognizer GPU reduction changed output shape at axis " + axis + ".");
            }
        }

        private static T LoadRequired<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException("Required recognizer GPU reduction parity asset is missing: " + path);
            return asset;
        }
#endif
    }
}
