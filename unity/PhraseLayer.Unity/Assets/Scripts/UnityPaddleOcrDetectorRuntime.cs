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
    /// Correctness-first PP-OCR detector runtime for Unity Inference Engine 2.2.x.
    ///
    /// This is deliberately a development baseline rather than the final Quest performance path:
    /// - resize/readback currently crosses GPU -> CPU;
    /// - normalization is performed on CPU into BGR NCHW floats;
    /// - detector output is synchronously read back to CPU;
    /// - DB contour/polygon extraction remains a separate backend.
    ///
    /// Keeping this path explicit gives us a reference implementation that can be parity-tested before replacing
    /// it with a compute-shader/functional-graph preprocessing path and asynchronous output readback.
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

            var model = ModelLoader.Load(modelAsset);
            if (model.inputs.Count != 1)
            {
                throw new InvalidOperationException(
                    "PP-OCR detector runtime currently requires exactly one model input; probe the imported ONNX before widening this contract.");
            }

            if (model.outputs.Count < 1)
                throw new InvalidOperationException("PP-OCR detector model must expose at least one output.");

            this.backendType = backendType;
            worker = new Worker(model, backendType);
        }

        public bool IsSupported => true;
        public BackendType BackendType => backendType;

        /// <summary>
        /// Runs the detector and returns its first output as a flat row-major float array.
        /// This method must be invoked from Unity's main thread because the development preprocessor uses
        /// Graphics.Blit, RenderTexture.active, Texture2D.ReadPixels and Unity object lifetime APIs.
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
                    "The development Unity texture preprocessor does not yet reproduce PaddleOCR's top-left small-image padding. " +
                    "Quest camera frames are above this threshold; add a dedicated padding stage before using tiny inputs.");
            }

            var inputValues = ReadAndNormalizeToBgrNchw(texture, resizeTransform, flipReadbackRows);
            var inputShape = new TensorShape(
                1,
                3,
                resizeTransform.ModelHeight,
                resizeTransform.ModelWidth);

            var inputTensor = new Tensor<float>(inputShape, inputValues);
            try
            {
                worker.Schedule(inputTensor);
                var outputTensor = worker.PeekOutput() as Tensor<float>;
                if (outputTensor == null)
                {
                    throw new InvalidOperationException(
                        "PP-OCR detector default output is not a float tensor. Capture UnityInferenceModelProbe output and update the runtime contract.");
                }

                // In Inference Engine 2.2.1 Tensor.ReadbackAndClone() is declared on the non-generic
                // Tensor base class and therefore returns Tensor. Cast the owned CPU clone back to the
                // expected element type before accessing DownloadToArray(). Keeping this explicit also
                // makes a future output dtype drift fail at the boundary rather than later in post-processing.
                var cpuTensor = outputTensor.ReadbackAndClone() as Tensor<float>;
                if (cpuTensor == null)
                    throw new InvalidOperationException("PP-OCR detector CPU readback did not preserve float tensor type.");
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

        private static float[] ReadAndNormalizeToBgrNchw(
            Texture source,
            PaddleDetResizeTransform resizeTransform,
            bool flipReadbackRows)
        {
            var width = resizeTransform.ModelWidth;
            var height = resizeTransform.ModelHeight;
            var renderTexture = RenderTexture.GetTemporary(
                width,
                height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);
            renderTexture.filterMode = FilterMode.Bilinear;

            var previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                Graphics.Blit(source, renderTexture);
                RenderTexture.active = renderTexture;

                readable = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readable.Apply(false, false);

                var pixels = readable.GetPixels32();
                var plane = checked(width * height);
                var values = new float[checked(plane * 3)];

                for (var y = 0; y < height; y++)
                {
                    var readbackY = flipReadbackRows ? height - 1 - y : y;
                    var sourceRow = readbackY * width;
                    var destinationRow = y * width;

                    for (var x = 0; x < width; x++)
                    {
                        var pixel = pixels[sourceRow + x];
                        var destinationIndex = destinationRow + x;
                        values[destinationIndex] = PaddleOcrV6TinyDetectionPreprocess.NormalizeChannel(pixel.b, 0);
                        values[plane + destinationIndex] = PaddleOcrV6TinyDetectionPreprocess.NormalizeChannel(pixel.g, 1);
                        values[(2 * plane) + destinationIndex] = PaddleOcrV6TinyDetectionPreprocess.NormalizeChannel(pixel.r, 2);
                    }
                }

                return values;
            }
            finally
            {
                RenderTexture.active = previous;
                if (readable != null)
                    UnityEngine.Object.Destroy(readable);
                RenderTexture.ReleaseTemporary(renderTexture);
            }
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
