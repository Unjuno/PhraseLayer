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
    /// Model assets and the recognition dictionary remain Inspector-assigned so unreviewed artifacts are not bundled by code.
    /// </summary>
    public sealed class UnityPaddleOcrBootstrapBehaviour : MonoBehaviour
    {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        [SerializeField] private OcrDebugRuntimeBehaviour runtimeDriver = default(OcrDebugRuntimeBehaviour);
        [SerializeField] private ModelAsset detectorModel = default(ModelAsset);
        [SerializeField] private ModelAsset recognizerModel = default(ModelAsset);
        [SerializeField] private TextAsset characterDictionary = default(TextAsset);
        [SerializeField] private bool useSpaceCharacter = true;
        [SerializeField] private float recognitionDropScore = 0.5f;
        [SerializeField] private int recognizerModelWidth = PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth;
        [SerializeField] private BackendType detectorBackend = BackendType.GPUCompute;
        [SerializeField] private BackendType recognizerBackend = BackendType.GPUCompute;

        private UnityPaddleOcrEngine engine;

        public bool IsInitialized => engine != null;
        public IOcrEngine Engine => engine;
        public int DictionaryTokenCount { get; private set; }

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
                throw new InvalidOperationException("Assign the revision-reviewed PP-OCR character dictionary TextAsset to the PP-OCR bootstrap.");
            if (recognitionDropScore < 0f || recognitionDropScore > 1f || float.IsNaN(recognitionDropScore) || float.IsInfinity(recognitionDropScore))
                throw new InvalidOperationException("Recognition drop score must be finite and within [0,1].");
            if (recognizerModelWidth <= 0)
                throw new InvalidOperationException("Recognizer model width must be greater than zero.");

            var dictionary = PaddleOcrCharacterDictionary.Parse(characterDictionary.text, useSpaceCharacter);
            DictionaryTokenCount = dictionary.Count;
            if (DictionaryTokenCount == 0)
            {
                throw new InvalidOperationException(
                    "The configured PP-OCR character dictionary contains no tokens. Confirm the reviewed dictionary asset and useSpaceCharacter setting.");
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
#endif
    }
}
