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
        public PaddleRecognizerRawOutput(PaddleRecResizeTransform resizeTransform, int[] outputShape, float[] outputValues)
        {
            ResizeTransform = resizeTransform ?? throw new ArgumentNullException(nameof(resizeTransform));
            OutputShape = outputShape ?? throw new ArgumentNullException(nameof(outputShape));
            OutputValues = outputValues ?? throw new ArgumentNullException(nameof(outputValues));
        }

        public PaddleRecResizeTransform ResizeTransform { get; }
        public int[] OutputShape { get; }
        public float[] OutputValues { get; }

        public PaddleCtcDecodeResult Decode(IReadOnlyList<string> characterDictionary)
        {
            if (characterDictionary == null) throw new ArgumentNullException(nameof(characterDictionary));
            PaddleOcrRuntimeContract.ValidateRecognizer(OutputShape, OutputValues, characterDictionary.Count);
            return PaddleCtcGreedyDecoder.DecodeFromPredictions(OutputValues, OutputShape[1], OutputShape[2], characterDictionary);
        }
    }

    /// <summary>
    /// GPU-reduced recognizer output. CPU receives one class index and maximum score per timestep; OutputShape
    /// describes the unreduced probability tensor metadata, not a downloaded probability matrix.
    /// </summary>
    public sealed class PaddleRecognizerReducedOutput
    {
        public PaddleRecognizerReducedOutput(PaddleRecResizeTransform resizeTransform, int[] outputShape, int[] classIndices, float[] maxScores)
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
            PaddleOcrRuntimeContract.ValidateRecognizerReduced(OutputShape, ClassIndices, MaxScores, characterDictionary.Count);
            return PaddleCtcGreedyDecoder.DecodeFromIndices(ClassIndices, MaxScores, characterDictionary);
        }
    }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
    /// <summary>
    /// Inference Engine 2.2.1 recognizer. Shader preprocessing feeds a BGR NCHW float tensor. ArgMax with
    /// selectLastIndex=false and ReduceMax are packed into float32 [1,time,2], class index then score. Core validates
    /// the real packed shape and the exact-integer class-count domain before interpreting the downloaded values.
    /// Only the reduced worker is retained. Execute creates a temporary full-output oracle for host verification.
    /// One explicit output readback per reduced crop is a code-path contract, not a measured latency guarantee.
    /// </summary>
    public sealed class UnityPaddleOcrRecognizerRuntime : IDisposable
    {
        public const string PreprocessShaderResourceName = "PaddleOcrRecognizerPreprocess";
        public const int ReducedValuesPerTimestep = PaddleCtcPackedOutput.ValuesPerTimestep;
        public const int ReducedReadbackOperationsPerCrop = 1;

        private readonly ModelAsset modelAsset;
        private readonly Worker reducedOutputWorker;
        private readonly BackendType backendType;
        private readonly Material preprocessMaterial;
        private bool disposed;

        public UnityPaddleOcrRecognizerRuntime(ModelAsset modelAsset, BackendType backendType = BackendType.GPUCompute)
        {
            if (modelAsset == null) throw new ArgumentNullException(nameof(modelAsset));
            var model = ModelLoader.Load(modelAsset);
            if (model.inputs.Count != 1)
                throw new InvalidOperationException("PP-OCR recognizer runtime currently requires exactly one model input; probe the imported ONNX before widening this contract.");
            if (model.inputs[0].dataType != DataType.Float)
                throw new InvalidOperationException("PP-OCR recognizer input must be float so reviewed GPU resize/pad/normalization can feed the imported model directly.");
            if (model.outputs.Count < 1)
                throw new InvalidOperationException("PP-OCR recognizer model must expose at least one output.");

            var reducedModel = BuildGpuReducedOutputModel(model);
            var material = CreateReviewedPreprocessMaterial();
            Worker reducedWorker = null;
            try
            {
                reducedWorker = new Worker(reducedModel, backendType);
            }
            catch
            {
                reducedWorker?.Dispose();
                UnityEngine.Object.Destroy(material);
                throw;
            }
            this.modelAsset = modelAsset;
            preprocessMaterial = material;
            reducedOutputWorker = reducedWorker;
            this.backendType = backendType;
        }

        public bool IsSupported => true;
        public BackendType BackendType => backendType;
        public bool UsesGpuTexturePreprocessing => true;
        public bool UsesGpuCtcReduction => backendType == BackendType.GPUCompute;
        public bool RetainsFullOutputWorker => false;
        public int CtcReadbackOperationsPerCrop => ReducedReadbackOperationsPerCrop;

        public static TextureTransform CreateReviewedTextureTransform(bool flipReadbackRows = true)
        {
            return new TextureTransform()
                .SetTensorLayout(TensorLayout.NCHW)
                .SetCoordOrigin(flipReadbackRows ? CoordOrigin.TopLeft : CoordOrigin.BottomLeft)
                .SetChannelSwizzle(ChannelSwizzle.BGRA);
        }

        public static Material CreateReviewedPreprocessMaterial()
        {
            var shader = Resources.Load<Shader>(PreprocessShaderResourceName);
            if (shader == null)
                throw new InvalidOperationException("Missing Resources/" + PreprocessShaderResourceName + ".shader. The GPU recognizer preprocessing shader must be bundled for Quest builds.");
            return new Material(shader)
            {
                name = "PhraseLayer PP-OCR Recognizer Preprocess Material",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

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
                throw new ArgumentException("Recognizer resize geometry must describe the exact rectified crop texture dimensions.", nameof(rectifiedCrop));

            var shape = inputTensor.shape;
            if (shape.rank != 4 || shape[0] != 1 || shape[1] != PaddleOcrV6TinyRecognitionPreprocess.Channels ||
                shape[2] != resizeTransform.ModelHeight || shape[3] != resizeTransform.ModelWidth)
                throw new ArgumentException("Recognizer input tensor must match [1,3,modelHeight,modelWidth] from the reviewed resize transform.", nameof(inputTensor));

            var normalizedTexture = RenderTexture.GetTemporary(
                resizeTransform.ModelWidth, resizeTransform.ModelHeight, 0,
                RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
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

        /// <summary>Host parity oracle only. No full-output worker is retained by the production runtime.</summary>
        public PaddleRecognizerRawOutput Execute(
            Texture rectifiedCrop,
            int modelWidth = PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth,
            bool flipReadbackRows = true)
        {
            ThrowIfDisposed();
            var resizeTransform = CreateResizeTransform(rectifiedCrop, modelWidth);
            var inputTensor = CreateInputTensor(resizeTransform);
            try
            {
                PopulateReviewedInputTensor(rectifiedCrop, resizeTransform, inputTensor, preprocessMaterial, flipReadbackRows);
                using (var parityWorker = new Worker(ModelLoader.Load(modelAsset), backendType))
                {
                    parityWorker.Schedule(inputTensor);
                    var outputTensor = parityWorker.PeekOutput() as Tensor<float>;
                    if (outputTensor == null)
                        throw new InvalidOperationException("PP-OCR recognizer default output is not a float tensor.");
                    var cpuTensor = outputTensor.ReadbackAndClone();
                    try
                    {
                        return new PaddleRecognizerRawOutput(resizeTransform, CopyShape(cpuTensor.shape), cpuTensor.DownloadToArray());
                    }
                    finally
                    {
                        cpuTensor.Dispose();
                    }
                }
            }
            finally
            {
                inputTensor.Dispose();
            }
        }

        /// <summary>Live path: exactly one explicit readback of the packed tensor; the full output is shape-only.</summary>
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
                PopulateReviewedInputTensor(rectifiedCrop, resizeTransform, inputTensor, preprocessMaterial, flipReadbackRows);
                reducedOutputWorker.Schedule(inputTensor);
                var packedTensor = reducedOutputWorker.PeekOutput(0) as Tensor<float>;
                var probabilityTensor = reducedOutputWorker.PeekOutput(1) as Tensor<float>;
                if (packedTensor == null || probabilityTensor == null)
                    throw new InvalidOperationException("PP-OCR recognizer reduced outputs must be a packed float [1,time,2] tensor plus a float probability shape witness.");

                var outputShape = CopyShape(probabilityTensor.shape);
                var packedShape = CopyShape(packedTensor.shape);
                PaddleCtcPackedOutput.ValidateShapes(outputShape, packedShape);
                var packedCpu = packedTensor.ReadbackAndClone();
                try
                {
                    var unpacked = PaddleCtcPackedOutput.Unpack(outputShape, packedShape, packedCpu.DownloadToArray());
                    return new PaddleRecognizerReducedOutput(resizeTransform, unpacked.OutputShape, unpacked.ClassIndices, unpacked.MaxScores);
                }
                finally
                {
                    packedCpu.Dispose();
                }
            }
            finally
            {
                inputTensor.Dispose();
            }
        }

        public PaddleCtcDecodeResult ExecuteAndDecode(Texture rectifiedCrop, IReadOnlyList<string> characterDictionary,
            int modelWidth = PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth, bool flipReadbackRows = true)
        {
            if (characterDictionary == null) throw new ArgumentNullException(nameof(characterDictionary));
            return Execute(rectifiedCrop, modelWidth, flipReadbackRows).Decode(characterDictionary);
        }

        public PaddleCtcDecodeResult ExecuteReducedAndDecode(Texture rectifiedCrop, IReadOnlyList<string> characterDictionary,
            int modelWidth = PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth, bool flipReadbackRows = true)
        {
            if (characterDictionary == null) throw new ArgumentNullException(nameof(characterDictionary));
            return ExecuteReduced(rectifiedCrop, modelWidth, flipReadbackRows).Decode(characterDictionary);
        }

        /// <summary>Production packing operators shared with the model-independent GPU tie/blank parity probe.</summary>
        public static FunctionalTensor PackReviewedCtcOutput(FunctionalTensor probabilities)
        {
            if (probabilities == null) throw new ArgumentNullException(nameof(probabilities));
            var classIndices = Functional.ArgMax(probabilities, dim: -1, keepdim: false);
            var maxScores = Functional.ReduceMax(probabilities, dim: -1, keepdim: false);
            return Functional.Concat(
                new[] { classIndices.Float().Unsqueeze(-1), maxScores.Unsqueeze(-1) },
                dim: -1);
        }

        private static Model BuildGpuReducedOutputModel(Model sourceModel)
        {
            var graph = new FunctionalGraph();
            var input = graph.AddInput(sourceModel, 0);
            var outputs = Functional.Forward(sourceModel, input);
            if (outputs == null || outputs.Length < 1 || outputs[0] == null)
                throw new InvalidOperationException("PP-OCR recognizer FunctionalGraph could not expose the imported model output.");
            var probabilities = outputs[0];
            var packed = PackReviewedCtcOutput(probabilities);
            graph.AddOutputs(packed, probabilities);
            return graph.Compile();
        }

        private static PaddleRecResizeTransform CreateResizeTransform(Texture rectifiedCrop, int modelWidth)
        {
            if (rectifiedCrop == null) throw new ArgumentNullException(nameof(rectifiedCrop));
            if (modelWidth <= 0) throw new ArgumentOutOfRangeException(nameof(modelWidth));
            return PaddleOcrV6TinyRecognitionPreprocess.CreateResizeTransform(rectifiedCrop.width, rectifiedCrop.height,
                modelWidth, PaddleOcrV6TinyRecognitionPreprocess.DefaultModelHeight);
        }

        private static Tensor<float> CreateInputTensor(PaddleRecResizeTransform resizeTransform)
        {
            return new Tensor<float>(new TensorShape(1, PaddleOcrV6TinyRecognitionPreprocess.Channels,
                resizeTransform.ModelHeight, resizeTransform.ModelWidth));
        }

        private static int[] CopyShape(TensorShape shape)
        {
            var dimensions = new int[shape.rank];
            for (var axis = 0; axis < dimensions.Length; axis++) dimensions[axis] = shape[axis];
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
            UnityEngine.Object.Destroy(preprocessMaterial);
        }
    }
#else
    public sealed class UnityPaddleOcrRecognizerRuntime : IDisposable
    {
        public bool IsSupported => false;
        public void Dispose() { }
    }
#endif
}
