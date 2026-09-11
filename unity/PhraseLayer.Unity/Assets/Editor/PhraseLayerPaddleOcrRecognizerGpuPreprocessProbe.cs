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
    /// Real-Unity numerical parity gate for the production PP-OCR recognizer input path.
    ///
    /// The fixture is intentionally 64x48 into a 96x48 model tensor. Because source and model heights match,
    /// ResizedWidth remains exactly 64: the left 64 tensor columns are one-to-one pixel-center samples and the
    /// right 32 columns are PaddleOCR normalized-zero padding. This isolates row origin, BGR swizzle,
    /// (x-0.5)/0.5 normalization and right-padding semantics from resize interpolation error.
    ///
    /// This requires a real graphics device and must not be run with -nographics.
    /// </summary>
    public static class PhraseLayerPaddleOcrRecognizerGpuPreprocessProbe
    {
        private const int SourceWidth = 64;
        private const int SourceHeight = PaddleOcrV6TinyRecognitionPreprocess.DefaultModelHeight;
        private const int ModelWidth = 96;
        private const float NormalizedTolerance = 0.04f;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        private static readonly Vector2Int[] SourceSamplePoints =
        {
            new Vector2Int(0, 0),
            new Vector2Int(SourceWidth - 1, 0),
            new Vector2Int(0, SourceHeight - 1),
            new Vector2Int(SourceWidth - 1, SourceHeight - 1),
            new Vector2Int(SourceWidth / 2, SourceHeight / 2),
            new Vector2Int(17, 11),
            new Vector2Int(43, 31),
        };

        private static readonly Vector2Int[] PaddingSamplePoints =
        {
            new Vector2Int(SourceWidth, 0),
            new Vector2Int(SourceWidth, SourceHeight - 1),
            new Vector2Int(ModelWidth - 1, 0),
            new Vector2Int(ModelWidth - 1, SourceHeight - 1),
            new Vector2Int((SourceWidth + ModelWidth) / 2, SourceHeight / 2),
        };
#endif

        [MenuItem("PhraseLayer/PP-OCR/Run Recognizer GPU Preprocess Parity Probe")]
        public static void Run()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            var resize = PaddleOcrV6TinyRecognitionPreprocess.CreateResizeTransform(
                SourceWidth,
                SourceHeight,
                ModelWidth,
                SourceHeight);
            if (resize.ResizedWidth != SourceWidth || resize.ModelHeight != SourceHeight || resize.ModelWidth != ModelWidth)
            {
                throw new InvalidOperationException(
                    "Recognizer GPU parity fixture must remain a one-to-one 64x48 image plus right padding into 96x48.");
            }

            var texture = CreatePatternTexture();
            var material = UnityPaddleOcrRecognizerRuntime.CreateReviewedPreprocessMaterial();
            try
            {
                var shape = new TensorShape(
                    1,
                    PaddleOcrV6TinyRecognitionPreprocess.Channels,
                    SourceHeight,
                    ModelWidth);
                var tensor = new Tensor<float>(shape);
                try
                {
                    UnityPaddleOcrRecognizerRuntime.PopulateReviewedInputTensor(
                        texture,
                        resize,
                        tensor,
                        material,
                        flipReadbackRows: true);

                    var cpu = tensor.ReadbackAndClone();
                    try
                    {
                        var values = cpu.DownloadToArray();
                        var maximumError = ValidateTensor(values);
                        Debug.Log(BuildReport(resize, maximumError));
                    }
                    finally
                    {
                        cpu.Dispose();
                    }
                }
                finally
                {
                    tensor.Dispose();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(texture);
            }
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve com.unity.ai.inference 2.2.1 before running recognizer GPU preprocess parity.");
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
                name = "PhraseLayer PP-OCR Recognizer GPU Preprocess Parity Pattern",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
            };

            var pixels = new Color32[SourceWidth * SourceHeight];
            for (var y = 0; y < SourceHeight; y++)
            {
                for (var x = 0; x < SourceWidth; x++)
                    pixels[y * SourceWidth + x] = PatternPixel(x, y);
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
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

        private static float ValidateTensor(float[] values)
        {
            var expectedLength = PaddleOcrV6TinyRecognitionPreprocess.Channels * SourceHeight * ModelWidth;
            if (values == null || values.Length != expectedLength)
            {
                throw new InvalidOperationException(
                    "Recognizer GPU preprocess tensor length mismatch: actual=" +
                    (values == null ? 0 : values.Length) + " expected=" + expectedLength + ".");
            }

            var maximumError = 0f;
            foreach (var point in SourceSamplePoints)
            {
                // Texture2D pattern coordinates are bottom-left based; production TopLeft tensor origin reverses Y.
                var sourceY = SourceHeight - 1 - point.y;
                var pixel = PatternPixel(point.x, sourceY);
                maximumError = Math.Max(maximumError, ValidateValue(
                    values[FlatIndex(0, point.x, point.y)],
                    PaddleOcrV6TinyRecognitionPreprocess.NormalizeChannel(pixel.b),
                    "B",
                    point));
                maximumError = Math.Max(maximumError, ValidateValue(
                    values[FlatIndex(1, point.x, point.y)],
                    PaddleOcrV6TinyRecognitionPreprocess.NormalizeChannel(pixel.g),
                    "G",
                    point));
                maximumError = Math.Max(maximumError, ValidateValue(
                    values[FlatIndex(2, point.x, point.y)],
                    PaddleOcrV6TinyRecognitionPreprocess.NormalizeChannel(pixel.r),
                    "R",
                    point));
            }

            foreach (var point in PaddingSamplePoints)
            {
                for (var channel = 0; channel < PaddleOcrV6TinyRecognitionPreprocess.Channels; channel++)
                {
                    maximumError = Math.Max(maximumError, ValidateValue(
                        values[FlatIndex(channel, point.x, point.y)],
                        0f,
                        "padding channel " + channel,
                        point));
                }
            }

            return maximumError;
        }

        private static float ValidateValue(float actual, float expected, string label, Vector2Int point)
        {
            if (float.IsNaN(actual) || float.IsInfinity(actual))
                throw new InvalidOperationException("Recognizer GPU preprocess " + label + " at " + point + " is non-finite.");

            var error = Math.Abs(actual - expected);
            if (error > NormalizedTolerance)
            {
                throw new InvalidOperationException(
                    "Recognizer GPU preprocess parity " + label + " failed at " + point +
                    ": actual=" + actual.ToString("F8") + " expected=" + expected.ToString("F8") +
                    " error=" + error.ToString("F8") + " tolerance=" + NormalizedTolerance.ToString("F8") + ".");
            }

            return error;
        }

        private static int FlatIndex(int channel, int x, int y)
        {
            return channel * SourceHeight * ModelWidth + y * ModelWidth + x;
        }

        private static string BuildReport(PaddleRecResizeTransform resize, float maximumError)
        {
            var builder = new StringBuilder(512);
            builder.AppendLine("PhraseLayer PP-OCR recognizer GPU preprocess parity PASS");
            builder.Append("source=").Append(SourceWidth).Append('x').Append(SourceHeight)
                .Append(" model=").Append(ModelWidth).Append('x').Append(SourceHeight)
                .Append(" resized_width=").Append(resize.ResizedWidth)
                .Append(" right_padding=").Append(resize.RightPaddingWidth)
                .AppendLine();
            builder.Append("layout=NCHW origin=TopLeft channels=BGR normalization=(x-0.5)/0.5 padding=normalized-zero")
                .AppendLine();
            builder.Append("normalized_max_abs_error=").Append(maximumError.ToString("F8"))
                .Append(" tolerance=").Append(NormalizedTolerance.ToString("F8"));
            return builder.ToString();
        }
#endif
    }
}
