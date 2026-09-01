using System;
using System.Text;
using PhraseLayer.Core.Inputs;
using UnityEditor;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Real-Unity numerical parity gate for the production PP-OCR detector input path.
    ///
    /// The probe deliberately uses a 736x736 oriented RGB pattern so no resize interpolation is involved. It verifies:
    /// - TextureConverter uses the production NCHW / TopLeft / BGRA transform;
    /// - downloaded raw tensor values preserve BGR byte values within the reviewed tolerance;
    /// - a FunctionalGraph built with the production ApplyReviewedNormalization helper matches the Core CPU contract.
    ///
    /// This requires a real graphics device and must not be run with -nographics.
    /// </summary>
    public static class PhraseLayerPaddleOcrGpuPreprocessProbe
    {
        private const int ProbeSize = PaddleOcrV6TinyDetectionPreprocess.DefaultLimitSideLength;
        private const float RawTolerance = 2.0f / 255.0f;
        private const float NormalizedTolerance = 0.04f;

        private static readonly Vector2Int[] SamplePoints =
        {
            new Vector2Int(0, 0),
            new Vector2Int(ProbeSize - 1, 0),
            new Vector2Int(0, ProbeSize - 1),
            new Vector2Int(ProbeSize - 1, ProbeSize - 1),
            new Vector2Int(ProbeSize / 2, ProbeSize / 2),
            new Vector2Int(37, 113),
            new Vector2Int(521, 289),
        };

        [MenuItem("PhraseLayer/PP-OCR/Run GPU Preprocess Parity Probe")]
        public static void Run()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            var resize = PaddleOcrV6TinyDetectionPreprocess.CreateResizeTransform(ProbeSize, ProbeSize);
            if (resize.ModelWidth != ProbeSize || resize.ModelHeight != ProbeSize || resize.UsesSmallImagePadding)
            {
                throw new InvalidOperationException(
                    "GPU preprocess parity fixture must remain a no-resize/no-padding PP-OCR detector input.");
            }

            var texture = CreatePatternTexture();
            try
            {
                var shape = new TensorShape(1, 3, ProbeSize, ProbeSize);
                var inputTensor = new Tensor<float>(shape);
                try
                {
                    TextureConverter.ToTensor(
                        texture,
                        inputTensor,
                        UnityPaddleOcrDetectorRuntime.CreateReviewedTextureTransform(flipReadbackRows: true));

                    var rawCpu = inputTensor.ReadbackAndClone();
                    try
                    {
                        var rawValues = rawCpu.DownloadToArray();
                        var rawMaximumError = ValidateRawTensor(rawValues);

                        var normalizationGraph = new FunctionalGraph();
                        var normalizationInput = normalizationGraph.AddInput<float>(shape, "input");
                        var normalized = UnityPaddleOcrDetectorRuntime.ApplyReviewedNormalization(normalizationInput);
                        var normalizationModel = normalizationGraph.Compile(normalized);
                        var normalizationWorker = new Worker(normalizationModel, BackendType.GPUCompute);
                        try
                        {
                            normalizationWorker.Schedule(inputTensor);
                            var output = normalizationWorker.PeekOutput() as Tensor<float>;
                            if (output == null)
                                throw new InvalidOperationException("GPU preprocess parity normalization output is not float.");

                            var normalizedCpu = output.ReadbackAndClone();
                            try
                            {
                                var normalizedValues = normalizedCpu.DownloadToArray();
                                var normalizedMaximumError = ValidateNormalizedTensor(normalizedValues);
                                Debug.Log(BuildReport(rawMaximumError, normalizedMaximumError));
                            }
                            finally
                            {
                                normalizedCpu.Dispose();
                            }
                        }
                        finally
                        {
                            normalizationWorker.Dispose();
                        }
                    }
                    finally
                    {
                        rawCpu.Dispose();
                    }
                }
                finally
                {
                    inputTensor.Dispose();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve com.unity.ai.inference 2.2.1 before running GPU preprocess parity.");
#endif
        }

        /// <summary>
        /// Batchmode entry point. A graphics device is mandatory; do not pass -nographics.
        /// </summary>
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
            var texture = new Texture2D(ProbeSize, ProbeSize, TextureFormat.RGBA32, mipChain: false, linear: false)
            {
                name = "PhraseLayer PP-OCR GPU Preprocess Parity Pattern",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
            };

            var pixels = new Color32[ProbeSize * ProbeSize];
            for (var y = 0; y < ProbeSize; y++)
            {
                for (var x = 0; x < ProbeSize; x++)
                    pixels[y * ProbeSize + x] = PatternPixel(x, y);
            }

            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return texture;
        }

        private static Color32 PatternPixel(int x, int y)
        {
            return new Color32(
                (byte)((x * 17 + y * 3 + 19) & 0xff),
                (byte)((x * 5 + y * 11 + 37) & 0xff),
                (byte)((x * 13 + y * 7 + 101) & 0xff),
                255);
        }

        private static float ValidateRawTensor(float[] values)
        {
            ValidateTensorLength(values, "raw");
            var maximumError = 0f;
            foreach (var point in SamplePoints)
            {
                var sourceY = ProbeSize - 1 - point.y;
                var pixel = PatternPixel(point.x, sourceY);
                maximumError = Math.Max(maximumError, ValidateValue(
                    values[FlatIndex(0, point.x, point.y)], pixel.b / 255f, RawTolerance, "raw B", point));
                maximumError = Math.Max(maximumError, ValidateValue(
                    values[FlatIndex(1, point.x, point.y)], pixel.g / 255f, RawTolerance, "raw G", point));
                maximumError = Math.Max(maximumError, ValidateValue(
                    values[FlatIndex(2, point.x, point.y)], pixel.r / 255f, RawTolerance, "raw R", point));
            }

            return maximumError;
        }

        private static float ValidateNormalizedTensor(float[] values)
        {
            ValidateTensorLength(values, "normalized");
            var maximumError = 0f;
            foreach (var point in SamplePoints)
            {
                var sourceY = ProbeSize - 1 - point.y;
                var pixel = PatternPixel(point.x, sourceY);
                maximumError = Math.Max(maximumError, ValidateValue(
                    values[FlatIndex(0, point.x, point.y)],
                    PaddleOcrV6TinyDetectionPreprocess.NormalizeChannel(pixel.b, 0),
                    NormalizedTolerance,
                    "normalized B",
                    point));
                maximumError = Math.Max(maximumError, ValidateValue(
                    values[FlatIndex(1, point.x, point.y)],
                    PaddleOcrV6TinyDetectionPreprocess.NormalizeChannel(pixel.g, 1),
                    NormalizedTolerance,
                    "normalized G",
                    point));
                maximumError = Math.Max(maximumError, ValidateValue(
                    values[FlatIndex(2, point.x, point.y)],
                    PaddleOcrV6TinyDetectionPreprocess.NormalizeChannel(pixel.r, 2),
                    NormalizedTolerance,
                    "normalized R",
                    point));
            }

            return maximumError;
        }

        private static float ValidateValue(
            float actual,
            float expected,
            float tolerance,
            string label,
            Vector2Int point)
        {
            if (float.IsNaN(actual) || float.IsInfinity(actual))
                throw new InvalidOperationException($"GPU preprocess parity {label} at {point} is non-finite: {actual}.");

            var error = Math.Abs(actual - expected);
            if (error > tolerance)
            {
                throw new InvalidOperationException(
                    $"GPU preprocess parity {label} failed at {point}: actual={actual:F8} expected={expected:F8} error={error:F8} tolerance={tolerance:F8}.");
            }

            return error;
        }

        private static void ValidateTensorLength(float[] values, string label)
        {
            var expected = 3 * ProbeSize * ProbeSize;
            if (values == null || values.Length != expected)
            {
                throw new InvalidOperationException(
                    $"GPU preprocess parity {label} tensor length mismatch: actual={(values == null ? 0 : values.Length)} expected={expected}.");
            }
        }

        private static int FlatIndex(int channel, int x, int y)
        {
            return channel * ProbeSize * ProbeSize + y * ProbeSize + x;
        }

        private static string BuildReport(float rawMaximumError, float normalizedMaximumError)
        {
            var builder = new StringBuilder(512);
            builder.AppendLine("PhraseLayer PP-OCR GPU preprocess parity PASS");
            builder.Append("size=").Append(ProbeSize).Append('x').Append(ProbeSize)
                .Append(" layout=NCHW origin=TopLeft channels=BGR")
                .AppendLine();
            builder.Append("raw_max_abs_error=").Append(rawMaximumError.ToString("F8"))
                .Append(" raw_tolerance=").Append(RawTolerance.ToString("F8"))
                .AppendLine();
            builder.Append("normalized_max_abs_error=").Append(normalizedMaximumError.ToString("F8"))
                .Append(" normalized_tolerance=").Append(NormalizedTolerance.ToString("F8"));
            return builder.ToString();
        }
#endif
    }
}
