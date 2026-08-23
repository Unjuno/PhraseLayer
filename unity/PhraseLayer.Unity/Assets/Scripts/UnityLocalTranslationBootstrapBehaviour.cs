using System;
using PhraseLayer.Core.Translation;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Fail-closed bridge from locally staged OPUS-MT assets to the Quest Read pipeline.
    ///
    /// No network or remote fallback exists. Initialization verifies the generated tokenizer/fixture bytes against
    /// the local staging manifest, parses the managed tokenizer, replays token-exact parity fixtures, validates
    /// Unity-visible ONNX model signatures, creates the local autoregressive backend, then injects the resulting
    /// translation engine into future Read encounters.
    /// </summary>
    public sealed class UnityLocalTranslationBootstrapBehaviour : MonoBehaviour
    {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        [SerializeField] private UnityLocalTranslationAssetGateBehaviour assetGate = null;
        [SerializeField] private QuestReadAssistanceDebugBehaviour readAssistance = null;
        [SerializeField] private TextAsset managedTokenizerManifest = null;
        [SerializeField] private TextAsset tokenizerFixtureManifest = null;
        [SerializeField] private ModelAsset encoderModel = null;
        [SerializeField] private ModelAsset decoderModel = null;
        [SerializeField] private bool initializeOnAwake = true;
        [SerializeField] private string lastReport = string.Empty;

        private UnityOpusMtAutoregressiveBackend ownedBackend;
        private ITranslationEngine configuredEngine;

        public bool IsSupported => true;
        public bool IsInitialized => configuredEngine != null;
        public string LastReport => lastReport;

        private void Awake()
        {
            if (!initializeOnAwake) return;
            InitializeLocalTranslation();
        }

        public string InitializeLocalTranslation()
        {
            if (configuredEngine != null)
                return lastReport;
            EnsureConfigured();

            UnityOpusMtAutoregressiveBackend candidateBackend = null;
            try
            {
                var bootstrapArtifacts = assetGate.ValidateBootstrapAssets(
                    managedTokenizerManifest,
                    tokenizerFixtureManifest);
                var tokenizer = ManagedSentencePieceManifest.ParseTokenizer(managedTokenizerManifest.text);
                var fixtures = TranslationTokenizerFixtureManifest.Parse(tokenizerFixtureManifest.text);
                var modelReport = UnityOpusMtModelProbe.ValidateAndBuildReport(encoderModel, decoderModel);

                candidateBackend = new UnityOpusMtAutoregressiveBackend(
                    encoderModel,
                    decoderModel,
                    BackendType.GPUCompute);
                var engine = OpusMtEnJapLocalEngineFactory.CreateReferenceEngine(
                    tokenizer,
                    fixtures,
                    candidateBackend);

                readAssistance.ConfigureTranslationEngine(engine);
                ownedBackend = candidateBackend;
                candidateBackend = null;
                configuredEngine = engine;
                lastReport =
                    "local translation bootstrap=ready" +
                    " tokenizer=" + bootstrapArtifacts.ManagedTokenizerManifest.Path +
                    " fixtures=" + bootstrapArtifacts.TokenizerFixtureManifest.Path +
                    " | " + modelReport;
                Debug.Log("PhraseLayer local translation bootstrap PASS: " + lastReport, this);
                return lastReport;
            }
            catch (Exception exception)
            {
                candidateBackend?.Dispose();
                lastReport = "local translation bootstrap=failed | " + exception.Message;
                Debug.LogException(exception, this);
                throw;
            }
        }

        private void OnDestroy()
        {
            configuredEngine = null;
            ownedBackend?.Dispose();
            ownedBackend = null;
        }

        private void EnsureConfigured()
        {
            if (assetGate == null)
                throw new InvalidOperationException("Assign UnityLocalTranslationAssetGateBehaviour before local translation initialization.");
            if (readAssistance == null)
                throw new InvalidOperationException("Assign QuestReadAssistanceDebugBehaviour before local translation initialization.");
            if (managedTokenizerManifest == null)
                throw new InvalidOperationException("Assign the parity-verified managed SentencePiece manifest.");
            if (tokenizerFixtureManifest == null)
                throw new InvalidOperationException("Assign the revision-pinned tokenizer parity fixture manifest.");
            if (encoderModel == null || decoderModel == null)
                throw new InvalidOperationException("Assign both locally imported OPUS-MT encoder and decoder ModelAssets.");
        }
#else
        [SerializeField] private string lastReport =
            "Local translation bootstrap unavailable: reviewed com.unity.ai.inference 2.2.x API gate is not active.";

        public bool IsSupported => false;
        public bool IsInitialized => false;
        public string LastReport => lastReport;

        public string InitializeLocalTranslation()
        {
            throw new NotSupportedException(lastReport);
        }
#endif
    }
}
