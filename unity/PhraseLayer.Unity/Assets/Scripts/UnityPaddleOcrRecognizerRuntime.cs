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
    /// Correctness-first PP-OCR recognizer runtime for Unity Inference Engine 2.2.x.
    ///
    /// Input must already be a rectified text crop. This baseline reproduces PaddleOCR's recognizer preprocessing:
    /// resize to height 48, preserve aspect ratio, cap width, normalize BGR channels to [-1,1], then right-pad
    /// the NCHW tensor with zeros. Both texture and model output are synchronously read back for parity testing.
    /// </summary>
    public sealed class UnityPaddleOcrRecognizerRuntime : IDisposable
    {
        private readonly Worker worker;
        private readonly BackendType backendType;
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
            if (model.outputs.Count < 1)
                throw new InvalidOperationException("PP-OCR recognizer model must expose at least one output.");

            this.backendType = backendType;
            worker = new Worker(model, backendType);
        }

        public bool IsSupported => true;
        public BackendType BackendType => backendType;

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

            var inputValues = ReadNormalizeAndPadToBgrNchw(
                rectifiedCrop,
                resizeTransform,
                flipReadbackRows);

            var inputShape = new TensorShape(
                1,
                PaddleOcrV6TinyRecognitionPreprocess.Channels,
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

        private static float[] ReadNormalizeAndPadToBgrNchw(
            Texture source,
            PaddleRecResizeTransform resizeTransform,
            bool flipReadbackRows)
        {
            var resizedWidth = resizeTransform.ResizedWidth;
            var height = resizeTransform.ModelHeight;
            var modelWidth = resizeTransform.ModelWidth;
            var renderTexture = RenderTexture.GetTemporary(
                resizedWidth,
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

                readable = new Texture2D(resizedWidth, height, TextureFormat.RGBA32, false, false);
                readable.ReadPixels(new Rect(0, 0, resizedWidth, height), 0, 0, false);
                readable.Apply(false, false);

                var pixels = readable.GetPixels32();
                var plane = checked(modelWidth * height);
                // PaddleOCR initializes the entire tensor with zeros, then writes normalized image columns on the left.
                var values = new float[checked(plane * PaddleOcrV6TinyRecognitionPreprocess.Channels)];

                for (var y = 0; y < height; y++)
                {
                    var readbackY = flipReadbackRows ? height - 1 - y : y;
                    var sourceRow = readbackY * resizedWidth;
                    var destinationRow = y * modelWidth;
                    for (var x = 0; x < resizedWidth; x++)
                    {
                        var pixel = pixels[sourceRow + x];
                        var destinationIndex = destinationRow + x;
                        values[destinationIndex] = PaddleOcrV6TinyRecognitionPreprocess.NormalizeChannel(pixel.b);
                        values[plane + destinationIndex] = PaddleOcrV6TinyRecognitionPreprocess.NormalizeChannel(pixel.g);
                        values[(2 * plane) + destinationIndex] = PaddleOcrV6TinyRecognitionPreprocess.NormalizeChannel(pixel.r);
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
            if (disposed) throw new ObjectDisposedException(nameof(UnityPaddleOcrRecognizerRuntime));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            worker.Dispose();
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
