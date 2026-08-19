using System;
using PhraseLayer.Core.Inputs;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Scene-facing bootstrap that owns the end-to-end PP-OCR engine and injects it into OcrDebugRuntimeBehaviour.
    /// Model assets, the generated recognition dictionary, and its revision manifest remain Inspector-assigned so
    /// unreviewed artifacts are not bundled by code and dictionary/model contract drift fails before inference.
    /// </summary>
    public sealed class UnityPaddleOcrBootstrapBehaviour : MonoBehaviour
    {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        [SerializeField] private OcrDebugRuntimeBehaviour runtimeDriver = default(OcrDebugRuntimeBehaviour);
        [SerializeField] private ModelAsset detectorModel = default(ModelAsset);
        [SerializeField] private ModelAsset recognizerModel = default(ModelAsset);
        [SerializeField] private TextAsset characterDictionary = default(TextAsset);
        [SerializeField] private TextAsset characterDictionaryManifest = default(TextAsset);
        [SerializeField] private bool useSpaceCharacter = true;
        [SerializeField] private float recognitionDropScore = 0.5f;
        [SerializeField] private int recognizerModelWidth = PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth;
        [SerializeField] private BackendType detectorBackend = BackendType.GPUCompute;
        [SerializeField] private BackendType recognizerBackend = BackendType.GPUCompute;

        private UnityPaddleOcrEngine engine;

        public bool IsInitialized => engine != null;
        public IOcrEngine Engine => engine;
        public int DictionaryTokenCount { get; private set; }
        public string DictionaryManifestReport { get; private set; } =
            "PP-OCR dictionary manifest not validated.";
        public string RuntimeContractReport => engine == null
            ? "PP-OCR engine not initialized; runtime model contract is unobserved."
            : engine.RuntimeContractReport;

        private void Awake()
        {
            Initialize();
        }

        public void Initialize()
        {
            if (engine != null) return;
            if (runtimeDriver == null)
                throw new InvalidOperationException("Assign OcrDebugRuntimeBehaviour to the PP-OCR bootstrap.");
            if (detectorModel == null)
                throw new InvalidOperationException("Assign the pinned PP-OCR detector ModelAsset to the PP-OCR bootstrap.");
            if (recognizerModel == null)
                throw new InvalidOperationException("Assign the pinned PP-OCR recognizer ModelAsset to the PP-OCR bootstrap.");
            if (characterDictionary == null)
                throw new InvalidOperationException("Assign the generated PP-OCR character dictionary TextAsset to the PP-OCR bootstrap.");
            if (characterDictionaryManifest == null)
                throw new InvalidOperationException("Assign the generated PP-OCR dictionary manifest TextAsset to the PP-OCR bootstrap.");
            if (recognitionDropScore < 0f || recognitionDropScore > 1f || float.IsNaN(recognitionDropScore) || float.IsInfinity(recognitionDropScore))
                throw new InvalidOperationException("Recognition drop score must be finite and within [0,1].");
            if (recognizerModelWidth <= 0)
                throw new InvalidOperationException("Recognizer model width must be greater than zero.");

            DictionaryManifestReport = UnityPaddleOcrDictionaryManifest.Validate(
                characterDictionary,
                characterDictionaryManifest,
                useSpaceCharacter);

            var dictionary = PaddleOcrCharacterDictionary.Parse(characterDictionary.text, useSpaceCharacter);
            DictionaryTokenCount = dictionary.Count;
            if (DictionaryTokenCount == 0)
            {
                throw new InvalidOperationException(
                    "The configured PP-OCR character dictionary contains no tokens. Confirm the generated dictionary/manifest assets.");
            }

            var created = new UnityPaddleOcrEngine(
                detectorModel,
                recognizerModel,
                dictionary,
                detectorBackend,
                recognizerBackend,
                dbSpec: null,
                recognitionDropScore: recognitionDropScore,
                recognizerModelWidth: recognizerModelWidth);

            try
            {
                runtimeDriver.ConfigureEngine(created);
                engine = created;
            }
            catch
            {
                created.Dispose();
                throw;
            }
        }

        private void OnDestroy()
        {
            engine?.Dispose();
            engine = null;
        }
#else
        public bool IsInitialized => false;
        public IOcrEngine Engine => null;
        public int DictionaryTokenCount => 0;
        public string DictionaryManifestReport => UnityPaddleOcrDictionaryManifest.UnsupportedReport;
        public string RuntimeContractReport =>
            "PP-OCR runtime contract unavailable: reviewed com.unity.ai.inference 2.2.x API gate is not active.";
#endif
    }
}
