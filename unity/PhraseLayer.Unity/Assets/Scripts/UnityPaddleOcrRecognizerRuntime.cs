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

    /// <summary>
    /// GPU-reduced recognizer result. The full probability tensor remains on the GPU; CPU receives only one winning
    /// class index and maximum score per timestep. OutputShape is copied from the unreduced GPU tensor metadata so the
    /// normal [1,time,class] + dictionary contract can still be validated before decoding.
    /// </summary>
    public sealed class PaddleRecognizerReducedOutput
    {
        public PaddleRecognizerReducedOutput(
            PaddleRecResizeTransform resizeTransform,
            int[] outputShape,
            int[] classIndices,
            float[] maxScores)
        {
            ResizeTransform = resizeTransform ?? throw new ArgumentNullException(nameof(resizeTransform));
            OutputShape = outputShape ?? throw new ArgumentNullException(nameof(outputShape));
            ClassIndices = classIndices ?? throw new ArgumentNullException(nameof(classIndices));
            MaxScores = maxScores ?? throw new ArgumentNullException(nameof(maxScores));
        }

        public PaddleRecResizeTransform ResizeTransform { get; }
        public int[] OutputShape { get; }
        public int[] ClassIndices { get; }
        public float[] MaxScores { get; }

        public PaddleCtcDecodeResult Decode(IReadOnlyList<string> characterDictionary)
        {
            if (characterDictionary == null) throw new ArgumentNullException(nameof(characterDictionary));
            return PaddleCtcGreedyDecoder.DecodeFromIndices(ClassIndices, MaxScores, characterDictionary);
        }
    }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
    /// <summary>
    /// PP-OCR recognizer runtime for Unity Inference Engine 2.2.x.
    ///
    /// Input must already be a GPU rectified text crop. Recognition preprocessing remains GPU-side:
    /// - a small shader performs aspect-preserving resize into the left ResizedWidth columns;
    /// - the same shader converts RGB samples back to byte-style encoded values when needed, applies PaddleOCR's
    ///   (x-0.5)/0.5 normalization, and writes exact normalized zero padding on the right;
    /// - TextureConverter writes the normalized texture directly into a BGR NCHW float tensor.
    ///
    /// Production CTC preparation also stays on the GPU: a FunctionalGraph wraps the imported recognizer with
    /// ArgMax(selectLastIndex=false) and ReduceMax along the class axis. CPU reads back only [time] class indices and
    /// [time] maximum scores. The unreduced Execute path is an explicit parity-only opt-in; production construction
    /// creates only the reduced worker so Quest does not retain a second recognizer worker/model execution plan.
    /// </summary>
    public sealed class UnityPaddleOcrRecognizerRuntime : IDisposable
    {
        public const string PreprocessShaderResourceName = "PaddleOcrRecognizerPreprocess";

        private readonly Worker fullOutputWorker;
        private readonly Worker reducedOutputWorker;
        private readonly BackendType backendType;
        private readonly Material preprocessMaterial;
        private bool disposed;

        public UnityPaddleOcrRecognizerRuntime(
            ModelAsset modelAsset,
            BackendType backendType = BackendType.GPUCompute,
            bool retainFullOutputParityWorker = false)
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

            var reducedModel = BuildGpuReducedOutputModel(model);
            var material = CreateReviewedPreprocessMaterial();
            Worker fullWorker = null;
            Worker reducedWorker = null;
            try
            {
                reducedWorker = new Worker(reducedModel, backendType);
                if (retainFullOutputParityWorker)
                    fullWorker = new Worker(model, backendType);
            }
            catch
            {
                fullWorker?.Dispose();
                reducedWorker?.Dispose();
                UnityEngine.Object.Destroy(material);
                throw;
            }

            preprocessMaterial = material;
            fullOutputWorker = fullWorker;
            reducedOutputWorker = reducedWorker;
            this.backendType = backendType;
        }

        public bool IsSupported => true;
        public BackendType BackendType => backendType;
        public bool UsesGpuTexturePreprocessing => true;
        public bool UsesGpuCtcReduction => true;
        public bool FullOutputParityPathAvailable => fullOutputWorker != null;

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

        /// <summary>
        /// Correctness/parity path. Copies the full [1,time,class] matrix to CPU. The constructor must explicitly set
        /// retainFullOutputParityWorker=true; production runtimes intentionally do not allocate this second worker.
        /// </summary>
        public PaddleRecognizerRawOutput Execute(
            Texture rectifiedCrop,
            int modelWidth = PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth,
            bool flipReadbackRows = true)
        {
            ThrowIfDisposed();
            if (fullOutputWorker == null)
            {
                throw new InvalidOperationException(
                    "Full recognizer output is parity-only. Construct UnityPaddleOcrRecognizerRuntime with retainFullOutputParityWorker=true for a reviewed real-Unity parity probe.");
            }

            var resizeTransform = CreateResizeTransform(rectifiedCrop, modelWidth);
            var inputTensor = CreateInputTensor(resizeTransform);
            try
            {
                PopulateReviewedInputTensor(
                    rectifiedCrop,
                    resizeTransform,
                    inputTensor,
                    preprocessMaterial,
                    flipReadbackRows);
                fullOutputWorker.Schedule(inputTensor);
                var outputTensor = fullOutputWorker.PeekOutput() as Tensor<float>;
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

        /// <summary>
        /// Production path. The wrapped model emits class indices and maximum scores for each timestep, plus the
        /// original probability tensor only as a GPU-resident shape witness. No [time,class] values are downloaded.
        /// Functional.ArgMax uses selectLastIndex=false, matching NumPy/Paddle's first-index-on-ties greedy behavior.
        /// </summary>
        public PaddleRecognizerReducedOutput ExecuteReduced(
            Texture rectifiedCrop,
            int modelWidth = PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth,
            bool flipReadbackRows = true)
        {
            ThrowIfDisposed();
            var resizeTransform = CreateResizeTransform(rectifiedCrop, modelWidth);
            var inputTensor = CreateInputTensor(resizeTransform);
            try
            {
                PopulateReviewedInputTensor(
                    rectifiedCrop,
                    resizeTransform,
                    inputTensor,
                    preprocessMaterial,
                    flipReadbackRows);
                reducedOutputWorker.Schedule(inputTensor);

                var indexTensor = reducedOutputWorker.PeekOutput(0) as Tensor<int>;
                var scoreTensor = reducedOutputWorker.PeekOutput(1) as Tensor<float>;
                var probabilityTensor = reducedOutputWorker.PeekOutput(2) as Tensor<float>;
                if (indexTensor == null || scoreTensor == null || probabilityTensor == null)
                {
                    throw new InvalidOperationException(
                        "PP-OCR recognizer reduced outputs must be int class indices, float max scores, and a float probability shape witness.");
                }

                var outputShape = CopyShape(probabilityTensor.shape);
                var indexCpu = indexTensor.ReadbackAndClone();
                try
                {
                    var scoreCpu = scoreTensor.ReadbackAndClone();
                    try
                    {
                        return new PaddleRecognizerReducedOutput(
                            resizeTransform,
                            outputShape,
                            indexCpu.DownloadToArray(),
                            scoreCpu.DownloadToArray());
                    }
                    finally
                    {
                        scoreCpu.Dispose();
                    }
                }
                finally
                {
                    indexCpu.Dispose();
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

        public PaddleCtcDecodeResult ExecuteReducedAndDecode(
            Texture rectifiedCrop,
            IReadOnlyList<string> characterDictionary,
            int modelWidth = PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth,
            bool flipReadbackRows = true)
        {
            if (characterDictionary == null) throw new ArgumentNullException(nameof(characterDictionary));
            return ExecuteReduced(rectifiedCrop, modelWidth, flipReadbackRows).Decode(characterDictionary);
        }

        private static Model BuildGpuReducedOutputModel(Model sourceModel)
        {
            var graph = new FunctionalGraph();
            var input = graph.AddInput(sourceModel, 0);
            var outputs = Functional.Forward(sourceModel, input);
            if (outputs == null || outputs.Length < 1 || outputs[0] == null)
                throw new InvalidOperationException("PP-OCR recognizer FunctionalGraph could not expose the imported model output.");

            var probabilities = outputs[0];
            var classIndices = Functional.ArgMax(probabilities, dim: -1, keepdim: false);
            var maxScores = Functional.ReduceMax(probabilities, dim: -1, keepdim: false);
            graph.AddOutputs(classIndices, maxScores, probabilities);
            return graph.Compile();
        }

        private static PaddleRecResizeTransform CreateResizeTransform(Texture rectifiedCrop, int modelWidth)
        {
            if (rectifiedCrop == null) throw new ArgumentNullException(nameof(rectifiedCrop));
            if (modelWidth <= 0) throw new ArgumentOutOfRangeException(nameof(modelWidth));
            return PaddleOcrV6TinyRecognitionPreprocess.CreateResizeTransform(
                rectifiedCrop.width,
                rectifiedCrop.height,
                modelWidth,
                PaddleOcrV6TinyRecognitionPreprocess.DefaultModelHeight);
        }

        private static Tensor<float> CreateInputTensor(PaddleRecResizeTransform resizeTransform)
        {
            return new Tensor<float>(new TensorShape(
                1,
                PaddleOcrV6TinyRecognitionPreprocess.Channels,
                resizeTransform.ModelHeight,
                resizeTransform.ModelWidth));
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
            reducedOutputWorker.Dispose();
            fullOutputWorker?.Dispose();
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
