using System;
using PhraseLayer.Core.Inputs;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Raw detector output copied to CPU memory together with the exact PP-OCR resize geometry used for the frame.
    /// The output shape is kept generic until the pinned ONNX model is imported and probed in real Unity.
    /// </summary>
    public sealed class PaddleDetectorRawOutput
    {
        public PaddleDetectorRawOutput(
            PaddleDetResizeTransform resizeTransform,
            int[] outputShape,
            float[] outputValues)
        {
            ResizeTransform = resizeTransform ?? throw new ArgumentNullException(nameof(resizeTransform));
            OutputShape = outputShape ?? throw new ArgumentNullException(nameof(outputShape));
            OutputValues = outputValues ?? throw new ArgumentNullException(nameof(outputValues));
        }

        public PaddleDetResizeTransform ResizeTransform { get; }
        public int[] OutputShape { get; }
        public float[] OutputValues { get; }
    }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
    /// <summary>
    /// PP-OCR detector runtime for Unity Inference Engine 2.2.x.
    ///
    /// Detector input preprocessing stays on the GPU:
    /// - TextureConverter performs bilinear resize directly from the camera texture into an NCHW tensor;
    /// - TextureTransform reproduces the reviewed BGR channel order and image-row origin;
    /// - a FunctionalGraph prepends the reviewed PP-OCR mean/std normalization to the detector model;
    /// - only the detector probability-map output is synchronously copied back to CPU for DB postprocessing.
    ///
    /// This removes Graphics.Blit/Texture2D.ReadPixels from the live detector-input path so the passthrough
    /// texture is sampled immediately when Execute is called instead of after a blocking CPU image readback.
    /// </summary>
    public sealed class UnityPaddleOcrDetectorRuntime : IDisposable
    {
        private readonly Worker worker;
        private readonly BackendType backendType;
        private bool disposed;

        public UnityPaddleOcrDetectorRuntime(
            ModelAsset modelAsset,
            BackendType backendType = BackendType.GPUCompute)
        {
            if (modelAsset == null) throw new ArgumentNullException(nameof(modelAsset));

            var sourceModel = ModelLoader.Load(modelAsset);
            if (sourceModel.inputs.Count != 1)
            {
                throw new InvalidOperationException(
                    "PP-OCR detector runtime currently requires exactly one model input; probe the imported ONNX before widening this contract.");
            }

            if (sourceModel.inputs[0].dataType != DataType.Float)
            {
                throw new InvalidOperationException(
                    "PP-OCR detector input must be float so reviewed BGR mean/std preprocessing can be composed ahead of the imported model.");
            }

            if (sourceModel.outputs.Count < 1)
                throw new InvalidOperationException("PP-OCR detector model must expose at least one output.");

            var runtimeModel = BuildGpuPreprocessedModel(sourceModel);
            this.backendType = backendType;
            worker = new Worker(runtimeModel, backendType);
        }

        public bool IsSupported => true;
        public BackendType BackendType => backendType;
        public bool UsesGpuTexturePreprocessing => true;

        /// <summary>
        /// Runs the detector and returns its first output as a flat row-major float array.
        /// This method remains Unity-thread-affine because TextureConverter and Worker scheduling submit graphics work.
        /// </summary>
        public PaddleDetectorRawOutput Execute(
            Texture texture,
            int sourceWidth,
            int sourceHeight,
            bool flipReadbackRows = true)
        {
            ThrowIfDisposed();
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            if (sourceWidth <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
            if (sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceHeight));
            if (texture.width != sourceWidth || texture.height != sourceHeight)
            {
                throw new ArgumentException(
                    "Frame metadata must match the Unity texture dimensions so PP-OCR geometry can be mapped back without ambiguity.",
                    nameof(texture));
            }

            var resizeTransform = PaddleOcrV6TinyDetectionPreprocess.CreateResizeTransform(sourceWidth, sourceHeight);
            if (resizeTransform.UsesSmallImagePadding)
            {
                throw new NotSupportedException(
                    "The GPU Unity texture preprocessor does not yet reproduce PaddleOCR's top-left small-image padding. " +
                    "Quest camera frames are above this threshold; add a dedicated padding stage before using tiny inputs.");
            }

            var inputShape = new TensorShape(
                1,
                3,
                resizeTransform.ModelHeight,
                resizeTransform.ModelWidth);
            var inputTensor = new Tensor<float>(inputShape);
            try
            {
                var textureTransform = new TextureTransform()
                    .SetTensorLayout(TensorLayout.NCHW)
                    .SetCoordOrigin(flipReadbackRows ? CoordOrigin.TopLeft : CoordOrigin.BottomLeft)
                    .SetChannelSwizzle(ChannelSwizzle.BGRA);

                TextureConverter.ToTensor(texture, inputTensor, textureTransform);
                worker.Schedule(inputTensor);

                var outputTensor = worker.PeekOutput() as Tensor<float>;
                if (outputTensor == null)
                {
                    throw new InvalidOperationException(
                        "PP-OCR detector default output is not a float tensor. Capture UnityInferenceModelProbe output and update the runtime contract.");
                }

                var cpuTensor = outputTensor.ReadbackAndClone();
                try
                {
                    var shape = CopyShape(cpuTensor.shape);
                    var values = cpuTensor.DownloadToArray();
                    return new PaddleDetectorRawOutput(resizeTransform, shape, values);
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

        private static Model BuildGpuPreprocessedModel(Model sourceModel)
        {
            var graph = new FunctionalGraph();
            var input = graph.AddInput(sourceModel, 0);

            // TextureConverter produces BGR values in [0,1]. Match the existing reviewed CPU contract exactly:
            // normalized[channel] = (value - mean[channel]) / std[channel].
            var mean = Functional.Constant(
                new TensorShape(1, 3, 1, 1),
                new[]
                {
                    PaddleOcrV6TinyDetectionPreprocess.MeanForChannel(0),
                    PaddleOcrV6TinyDetectionPreprocess.MeanForChannel(1),
                    PaddleOcrV6TinyDetectionPreprocess.MeanForChannel(2)
                });
            var standardDeviation = Functional.Constant(
                new TensorShape(1, 3, 1, 1),
                new[]
                {
                    PaddleOcrV6TinyDetectionPreprocess.StandardDeviationForChannel(0),
                    PaddleOcrV6TinyDetectionPreprocess.StandardDeviationForChannel(1),
                    PaddleOcrV6TinyDetectionPreprocess.StandardDeviationForChannel(2)
                });

            var normalized = (input - mean) / standardDeviation;
            var outputs = Functional.Forward(sourceModel, normalized);
            graph.AddOutputs(outputs);
            return graph.Compile();
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
            if (disposed) throw new ObjectDisposedException(nameof(UnityPaddleOcrDetectorRuntime));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            worker.Dispose();
        }
    }
#else
    /// <summary>
    /// Host-CI fallback. Real detector execution is compiled only when the reviewed Inference Engine 2.2.x gate is active.
    /// </summary>
    public sealed class UnityPaddleOcrDetectorRuntime : IDisposable
    {
        public bool IsSupported => false;

        public void Dispose()
        {
        }
    }
#endif
}
