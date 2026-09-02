using System;
using System.Collections.Generic;
using PhraseLayer.Core.Inputs;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
    public sealed class PaddleRecognizerRawOutput
    {
        public PaddleRecognizerRawOutput(
            PaddleRecResizeTransform resizeTransform,
            int[] outputShape,
            float[] outputValues)
        {
            ResizeTransform = resizeTransform ?? throw new ArgumentNullException(nameof(resizeTransform));
            OutputShape = outputShape ?? throw new ArgumentNullException(nameof(outputShape));
            OutputValues = outputValues ?? throw new ArgumentNullException(nameof(outputValues));
        }

        public PaddleRecResizeTransform ResizeTransform { get; }
        public int[] OutputShape { get; }
        public float[] OutputValues { get; }

        /// <summary>
        /// Decodes a prob/logit matrix only when the imported model proves the expected [1,time,class] output contract.
        /// The external dictionary excludes the blank token; Core inserts blank conceptually at class index 0.
        /// </summary>
        public PaddleCtcDecodeResult Decode(IReadOnlyList<string> characterDictionary)
        {
            if (characterDictionary == null) throw new ArgumentNullException(nameof(characterDictionary));
            if (OutputShape.Length != 3 || OutputShape[0] != 1)
            {
                throw new InvalidOperationException(
                    "Recognizer output must be [1,time,class] before CTC decoding. Capture UnityInferenceModelProbe output and update the runtime contract if the pinned ONNX differs.");
            }

            var timeSteps = OutputShape[1];
            var classCount = OutputShape[2];
            return PaddleCtcGreedyDecoder.DecodeFromPredictions(
                OutputValues,
                timeSteps,
                classCount,
                characterDictionary);
        }
    }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
    /// <summary>
    /// PP-OCR recognizer runtime for Unity Inference Engine 2.2.x.
    ///
    /// Input must already be a GPU rectified text crop. Recognition preprocessing now remains GPU-resident:
    /// - a small shader performs aspect-preserving resize into the left ResizedWidth columns;
    /// - the same shader converts RGB samples back to byte-style encoded values when needed, applies PaddleOCR's
    ///   (x-0.5)/0.5 normalization, and writes exact normalized zero padding on the right;
    /// - TextureConverter writes the normalized texture directly into a BGR NCHW float tensor;
    /// - only the recognizer probability matrix is synchronously copied to CPU for CTC decoding.
    ///
    /// No Texture2D.ReadPixels/GetPixels32 input readback remains in this runtime. The shader/TextureConverter numeric
    /// contract is shared with a real-Unity parity probe; Quest performance still requires physical-device measurement.
    /// </summary>
    public sealed class UnityPaddleOcrRecognizerRuntime : IDisposable
    {
        public const string PreprocessShaderResourceName = "PaddleOcrRecognizerPreprocess";

        private readonly Worker worker;
        private readonly BackendType backendType;
        private readonly Material preprocessMaterial;
        private bool disposed;

        public UnityPaddleOcrRecognizerRuntime(
            ModelAsset modelAsset,
            BackendType backendType = BackendType.GPUCompute)
        {
            if (modelAsset == null) throw new ArgumentNullException(nameof(modelAsset));

            var model = ModelLoader.Load(modelAsset);
            if (model.inputs.Count != 1)
            {
                throw new InvalidOperationException(
                    "PP-OCR recognizer runtime currently requires exactly one model input; probe the imported ONNX before widening this contract.");
            }
            if (model.inputs[0].dataType != DataType.Float)
            {
                throw new InvalidOperationException(
                    "PP-OCR recognizer input must be float so reviewed GPU resize/pad/normalization can feed the imported model directly.");
            }
            if (model.outputs.Count < 1)
                throw new InvalidOperationException("PP-OCR recognizer model must expose at least one output.");

            preprocessMaterial = CreateReviewedPreprocessMaterial();
            this.backendType = backendType;
            worker = new Worker(model, backendType);
        }

        public bool IsSupported => true;
        public BackendType BackendType => backendType;
        public bool UsesGpuTexturePreprocessing => true;

        /// <summary>
        /// The exact TextureConverter transform shared by production recognizer input and the real-Unity parity probe.
        /// The preprocess shader writes normalized RGB values; BGRA swizzle produces PaddleOCR's B,G,R channel planes.
        /// </summary>
        public static TextureTransform CreateReviewedTextureTransform(bool flipReadbackRows = true)
        {
            return new TextureTransform()
                .SetTensorLayout(TensorLayout.NCHW)
                .SetCoordOrigin(flipReadbackRows ? CoordOrigin.TopLeft : CoordOrigin.BottomLeft)
                .SetChannelSwizzle(ChannelSwizzle.BGRA);
        }

        /// <summary>
        /// Loads the committed recognizer preprocessing shader from Resources. Keeping shader lookup centralized makes
        /// the production runtime and Editor parity probe fail on the same missing/stripped asset contract.
        /// </summary>
        public static Material CreateReviewedPreprocessMaterial()
        {
            var shader = Resources.Load<Shader>(PreprocessShaderResourceName);
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "Missing Resources/" + PreprocessShaderResourceName +
                    ".shader. The GPU recognizer preprocessing shader must be bundled for Quest builds.");
            }

            return new Material(shader)
            {
                name = "PhraseLayer PP-OCR Recognizer Preprocess Material",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        /// <summary>
        /// Populates an already-allocated [1,3,48,modelWidth] tensor using the reviewed production preprocessing path.
        /// This method is shared with the real-Unity numerical parity probe so resize, padding, normalization, channel
        /// order and row origin cannot silently drift between test and production code.
        /// </summary>
        public static void PopulateReviewedInputTensor(
            Texture rectifiedCrop,
            PaddleRecResizeTransform resizeTransform,
            Tensor<float> inputTensor,
            Material material,
            bool flipReadbackRows = true)
        {
            if (rectifiedCrop == null) throw new ArgumentNullException(nameof(rectifiedCrop));
            if (resizeTransform == null) throw new ArgumentNullException(nameof(resizeTransform));
            if (inputTensor == null) throw new ArgumentNullException(nameof(inputTensor));
            if (material == null) throw new ArgumentNullException(nameof(material));
            if (rectifiedCrop.width != resizeTransform.SourceWidth || rectifiedCrop.height != resizeTransform.SourceHeight)
            {
                throw new ArgumentException(
                    "Recognizer resize geometry must describe the exact rectified crop texture dimensions.",
                    nameof(rectifiedCrop));
            }

            var shape = inputTensor.shape;
            if (shape.rank != 4 ||
                shape[0] != 1 ||
                shape[1] != PaddleOcrV6TinyRecognitionPreprocess.Channels ||
                shape[2] != resizeTransform.ModelHeight ||
                shape[3] != resizeTransform.ModelWidth)
            {
                throw new ArgumentException(
                    "Recognizer input tensor must match [1,3,modelHeight,modelWidth] from the reviewed resize transform.",
                    nameof(inputTensor));
            }

            var normalizedTexture = RenderTexture.GetTemporary(
                resizeTransform.ModelWidth,
                resizeTransform.ModelHeight,
                0,
                RenderTextureFormat.ARGBHalf,
                RenderTextureReadWrite.Linear);
            normalizedTexture.filterMode = FilterMode.Bilinear;
            normalizedTexture.wrapMode = TextureWrapMode.Clamp;

            try
            {
                material.SetFloat("_ValidRatio", (float)resizeTransform.ValidRatio);
                Graphics.Blit(rectifiedCrop, normalizedTexture, material, 0);
                TextureConverter.ToTensor(
                    normalizedTexture,
                    inputTensor,
                    CreateReviewedTextureTransform(flipReadbackRows));
            }
            finally
            {
                RenderTexture.ReleaseTemporary(normalizedTexture);
            }
        }

        public PaddleRecognizerRawOutput Execute(
            Texture rectifiedCrop,
            int modelWidth = PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth,
            bool flipReadbackRows = true)
        {
            ThrowIfDisposed();
            if (rectifiedCrop == null) throw new ArgumentNullException(nameof(rectifiedCrop));
            if (modelWidth <= 0) throw new ArgumentOutOfRangeException(nameof(modelWidth));

            var resizeTransform = PaddleOcrV6TinyRecognitionPreprocess.CreateResizeTransform(
                rectifiedCrop.width,
                rectifiedCrop.height,
                modelWidth,
                PaddleOcrV6TinyRecognitionPreprocess.DefaultModelHeight);

            var inputShape = new TensorShape(
                1,
                PaddleOcrV6TinyRecognitionPreprocess.Channels,
                resizeTransform.ModelHeight,
                resizeTransform.ModelWidth);
            var inputTensor = new Tensor<float>(inputShape);
            try
            {
                PopulateReviewedInputTensor(
                    rectifiedCrop,
                    resizeTransform,
                    inputTensor,
                    preprocessMaterial,
                    flipReadbackRows);
                worker.Schedule(inputTensor);
                var outputTensor = worker.PeekOutput() as Tensor<float>;
                if (outputTensor == null)
                {
                    throw new InvalidOperationException(
                        "PP-OCR recognizer default output is not a float tensor. Capture UnityInferenceModelProbe output and update the runtime contract.");
                }

                var cpuTensor = outputTensor.ReadbackAndClone();
                try
                {
                    return new PaddleRecognizerRawOutput(
                        resizeTransform,
                        CopyShape(cpuTensor.shape),
                        cpuTensor.DownloadToArray());
                }
                finally
                {
                    cpuTensor.Dispose();
                }
            }
            finally
            {
                inputTensor.Dispose();
            }
        }

        public PaddleCtcDecodeResult ExecuteAndDecode(
            Texture rectifiedCrop,
            IReadOnlyList<string> characterDictionary,
            int modelWidth = PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth,
            bool flipReadbackRows = true)
        {
            if (characterDictionary == null) throw new ArgumentNullException(nameof(characterDictionary));
            return Execute(rectifiedCrop, modelWidth, flipReadbackRows).Decode(characterDictionary);
        }

        private static int[] CopyShape(TensorShape shape)
        {
            var dimensions = new int[shape.rank];
            for (var axis = 0; axis < dimensions.Length; axis++)
                dimensions[axis] = shape[axis];
            return dimensions;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(UnityPaddleOcrRecognizerRuntime));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            worker.Dispose();
            UnityEngine.Object.Destroy(preprocessMaterial);
        }
    }
#else
    public sealed class UnityPaddleOcrRecognizerRuntime : IDisposable
    {
        public bool IsSupported => false;

        public void Dispose()
        {
        }
    }
#endif
}
