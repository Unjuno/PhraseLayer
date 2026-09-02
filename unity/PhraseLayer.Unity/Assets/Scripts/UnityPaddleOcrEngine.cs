using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhraseLayer.Core.Inputs;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
    /// <summary>
    /// Correctness-first end-to-end PP-OCR engine for PhraseLayer.
    ///
    /// Pipeline:
    /// Unity Texture -> detector Worker -> CPU DB quad decoder -> Paddle reading-order sort ->
    /// GPU perspective crop -> recognizer Worker -> GPU ArgMax/ReduceMax -> CPU CTC decode -> OcrObservation/OcrRegion.
    ///
    /// Detector and recognizer image preprocessing stay GPU-side. Detector probability maps still return to CPU for DB
    /// post-processing. Recognizer probability matrices stay GPU-side in the live path: only one class index and maximum
    /// score per timestep are read back for CTC decoding. The full recognizer matrix path remains available only for the
    /// real-Unity contract/parity gate that must precede packaging/device execution.
    /// </summary>
    public sealed class UnityPaddleOcrEngine : IOcrEngine, IDisposable
    {
        private readonly UnityPaddleOcrDetectorRuntime detector;
        private readonly UnityPaddleOcrRecognizerRuntime recognizer;
        private readonly UnityPaddleOcrCropRectifier cropRectifier;
        private readonly string[] characterDictionary;
        private readonly PaddleDbPostprocessSpec dbSpec;
        private readonly double recognitionDropScore;
        private readonly int recognizerModelWidth;
        private readonly int ownerThreadId;
        private PaddleDetectorRuntimeContract latestDetectorContract;
        private PaddleRecognizerRuntimeContract latestRecognizerContract;
        private bool disposed;

        public UnityPaddleOcrEngine(
            ModelAsset detectorModel,
            ModelAsset recognizerModel,
            IReadOnlyList<string> characterDictionary,
            BackendType detectorBackend = BackendType.GPUCompute,
            BackendType recognizerBackend = BackendType.GPUCompute,
            PaddleDbPostprocessSpec dbSpec = null,
            double recognitionDropScore = PaddleOcrObservationAssembler.DefaultRecognitionDropScore,
            int recognizerModelWidth = PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth)
        {
            if (detectorModel == null) throw new ArgumentNullException(nameof(detectorModel));
            if (recognizerModel == null) throw new ArgumentNullException(nameof(recognizerModel));
            this.characterDictionary = SnapshotDictionary(characterDictionary);
            this.dbSpec = dbSpec ?? PaddleDbPostprocessSpec.V6Tiny();
            ValidateDropScore(recognitionDropScore);
            if (recognizerModelWidth <= 0) throw new ArgumentOutOfRangeException(nameof(recognizerModelWidth));

            this.recognitionDropScore = recognitionDropScore;
            this.recognizerModelWidth = recognizerModelWidth;
            ownerThreadId = Thread.CurrentThread.ManagedThreadId;

            detector = new UnityPaddleOcrDetectorRuntime(detectorModel, detectorBackend);
            try
            {
                recognizer = new UnityPaddleOcrRecognizerRuntime(recognizerModel, recognizerBackend);
                try
                {
                    cropRectifier = new UnityPaddleOcrCropRectifier();
                }
                catch
                {
                    recognizer.Dispose();
                    throw;
                }
            }
            catch
            {
                detector.Dispose();
                throw;
            }
        }

        public bool IsSupported => true;
        public double RecognitionDropScore => recognitionDropScore;
        public int RecognizerModelWidth => recognizerModelWidth;
        public PaddleDetectorRuntimeContract LatestDetectorContract => latestDetectorContract;
        public PaddleRecognizerRuntimeContract LatestRecognizerContract => latestRecognizerContract;
        public bool UsesGpuRecognizerCtcReduction => recognizer.UsesGpuCtcReduction;
        public string RuntimeContractReport => PaddleOcrRuntimeContract.BuildReport(
            latestDetectorContract,
            latestRecognizerContract,
            characterDictionary.Length);

        /// <summary>
        /// Executes synchronously and returns an already-completed Task to satisfy the platform-neutral IOcrEngine contract.
        /// Do not wrap this method in Task.Run: the reference Unity graphics path is main-thread bound.
        /// </summary>
        public Task<OcrObservation> RecognizeAsync(
            ImageFrame frame,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            ThrowIfDisposed();
            AssertOwnerThread();
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            cancellationToken.ThrowIfCancellationRequested();

            var payload = frame.NativePayload as UnityTextureFramePayload;
            if (payload == null)
            {
                throw new ArgumentException(
                    "UnityPaddleOcrEngine requires ImageFrame.NativePayload to be UnityTextureFramePayload so the detector can consume the camera texture without an extra frame copy.",
                    nameof(frame));
            }

            var texture = payload.Texture;
            if (texture.width != frame.Width || texture.height != frame.Height)
            {
                throw new ArgumentException(
                    "ImageFrame dimensions must match UnityTextureFramePayload.Texture dimensions.",
                    nameof(frame));
            }

            var detectorOutput = detector.Execute(texture, frame.Width, frame.Height);
            latestDetectorContract = PaddleOcrRuntimeContract.ValidateDetector(
                detectorOutput.OutputShape,
                detectorOutput.OutputValues);

            var detections = PaddleOcrReadingOrder.Sort(detectorOutput.DecodeV6TinyQuads(dbSpec));
            if (detections.Count == 0)
            {
                return Task.FromResult(PaddleOcrObservationAssembler.Assemble(
                    Array.Empty<PaddleOcrRecognizedCandidate>(),
                    recognitionDropScore));
            }

            var recognized = new List<PaddleOcrRecognizedCandidate>(detections.Count);
            for (var index = 0; index < detections.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var detection = detections[index];
                using (var crop = cropRectifier.Rectify(texture, detection.ImageBounds))
                {
                    var recognizerOutput = recognizer.ExecuteReduced(
                        crop.Texture,
                        recognizerModelWidth);
                    latestRecognizerContract = PaddleOcrRuntimeContract.ValidateRecognizerReduced(
                        recognizerOutput.OutputShape,
                        recognizerOutput.ClassIndices,
                        recognizerOutput.MaxScores,
                        characterDictionary.Length);
                    var decoded = recognizerOutput.Decode(characterDictionary);

                    // OcrObservation/OcrRegion confidence is explicitly constrained to [0,1]. Do not clamp
                    // unverified logits into that domain; fail until the pinned imported recognizer output proves the
                    // probability contract in the real-Unity full-output parity gate.
                    ValidateRecognizerConfidence(decoded.Confidence);

                    recognized.Add(new PaddleOcrRecognizedCandidate(
                        decoded.Text,
                        decoded.Confidence,
                        detection.Score,
                        detection.ImageBounds));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PaddleOcrObservationAssembler.Assemble(
                recognized,
                recognitionDropScore));
        }

        private static string[] SnapshotDictionary(IReadOnlyList<string> dictionary)
        {
            if (dictionary == null) throw new ArgumentNullException(nameof(dictionary));
            var snapshot = new string[dictionary.Count];
            for (var index = 0; index < dictionary.Count; index++)
            {
                snapshot[index] = dictionary[index] ?? throw new ArgumentException(
                    "PP-OCR character dictionary cannot contain null tokens.",
                    nameof(dictionary));
            }
            return snapshot;
        }

        private static void ValidateDropScore(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
                throw new ArgumentOutOfRangeException(nameof(value));
        }

        private static void ValidateRecognizerConfidence(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
            {
                throw new InvalidOperationException(
                    "Recognizer CTC confidence is outside [0,1]. The pinned imported ONNX output has not proven the probability contract required by OcrObservation. Probe the real model output instead of clamping logits.");
            }
        }

        private void AssertOwnerThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThreadId)
            {
                throw new InvalidOperationException(
                    "UnityPaddleOcrEngine must run on the same Unity thread on which it was created; its texture preprocessing and crop path uses main-thread Unity graphics APIs.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(UnityPaddleOcrEngine));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cropRectifier.Dispose();
            recognizer.Dispose();
            detector.Dispose();
        }
    }
#else
    /// <summary>
    /// Host-CI fallback. The real end-to-end engine is compiled only with the reviewed Inference Engine 2.2.x gate.
    /// </summary>
    public sealed class UnityPaddleOcrEngine : IOcrEngine, IDisposable
    {
        public bool IsSupported => false;
        public string RuntimeContractReport =>
            "PP-OCR runtime contract unavailable: reviewed com.unity.ai.inference 2.2.x API gate is not active.";

        public Task<OcrObservation> RecognizeAsync(
            ImageFrame frame,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException(
                "UnityPaddleOcrEngine requires the reviewed com.unity.ai.inference 2.2.x API gate.");
        }

        public void Dispose()
        {
        }
    }
#endif
}
