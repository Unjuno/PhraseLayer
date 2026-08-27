using System;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Git-ignored Resources asset that carries reviewed local PP-OCR references into a Player build.
    /// The asset is generated only from already-staged local OCR files; PhraseLayer never downloads model weights
    /// or silently substitutes remote inference at runtime.
    /// </summary>
    public sealed class UnityLocalOcrRuntimeConfig : ScriptableObject
    {
        public const string ResourcesName = "PhraseLayerLocalOcrRuntimeConfig";

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        [SerializeField] private ModelAsset detectorModel = default(ModelAsset);
        [SerializeField] private ModelAsset recognizerModel = default(ModelAsset);
        [SerializeField] private TextAsset characterDictionary = default(TextAsset);
        [SerializeField] private TextAsset characterDictionaryManifest = default(TextAsset);
        [SerializeField] private bool useSpaceCharacter = true;
        [SerializeField] private float recognitionDropScore = 0.5f;
        [SerializeField] private int recognizerModelWidth = PhraseLayer.Core.Inputs.PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth;
        [SerializeField] private BackendType detectorBackend = BackendType.GPUCompute;
        [SerializeField] private BackendType recognizerBackend = BackendType.GPUCompute;

        public bool IsConfigured =>
            detectorModel != null &&
            recognizerModel != null &&
            characterDictionary != null &&
            characterDictionaryManifest != null &&
            recognitionDropScore >= 0f &&
            recognitionDropScore <= 1f &&
            !float.IsNaN(recognitionDropScore) &&
            !float.IsInfinity(recognitionDropScore) &&
            recognizerModelWidth > 0;

        public string Status => IsConfigured
            ? "Local PP-OCR runtime config is ready."
            : "Local PP-OCR runtime config is incomplete.";

        public void ConfigureBootstrap(
            UnityPaddleOcrBootstrapBehaviour bootstrap,
            OcrDebugRuntimeBehaviour runtimeDriver)
        {
            if (bootstrap == null) throw new ArgumentNullException(nameof(bootstrap));
            if (runtimeDriver == null) throw new ArgumentNullException(nameof(runtimeDriver));
            if (!IsConfigured)
                throw new InvalidOperationException("Cannot configure PP-OCR bootstrap from an incomplete runtime config.");

            bootstrap.Configure(
                runtimeDriver,
                detectorModel,
                recognizerModel,
                characterDictionary,
                characterDictionaryManifest,
                useSpaceCharacter,
                recognitionDropScore,
                recognizerModelWidth,
                detectorBackend,
                recognizerBackend);
        }
#else
        public bool IsConfigured => false;
        public string Status =>
            "Local PP-OCR runtime config is unsupported because the reviewed Unity Inference 2.2.x gate is inactive.";

        public void ConfigureBootstrap(
            UnityPaddleOcrBootstrapBehaviour bootstrap,
            OcrDebugRuntimeBehaviour runtimeDriver)
        {
            throw new NotSupportedException(Status);
        }
#endif
    }
}
