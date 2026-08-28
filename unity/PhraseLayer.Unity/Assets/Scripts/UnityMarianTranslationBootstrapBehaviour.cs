using System;
using PhraseLayer.Core.Translation;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Explicit scene bootstrap for replacing the demo dictionary with the reviewed offline Marian stack.
    ///
    /// Configure this component with the three imported ONNX ModelAssets and locally staged tokenizer Resources.
    /// Awake runs before PhraseLayerDemoBehaviour.Start, so the translation engine override is installed before the
    /// semantic/learner pipeline is constructed. If any required runtime asset is missing, the demo is disabled
    /// instead of silently falling back to the dictionary and pretending model-backed translation is active.
    /// </summary>
    public sealed class UnityMarianTranslationBootstrapBehaviour : MonoBehaviour
    {
        [SerializeField] private PhraseLayerDemoBehaviour demo = null;
        [SerializeField] private string lastStatus = "Marian runtime not initialized.";

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        [SerializeField] private string tokenizerResourceRoot = "LocalTranslationAssets";
        [SerializeField] private int maximumSourceTokens = 128;
        [SerializeField] private int maximumTargetTokens = 128;
        [SerializeField] private bool useDeviceResidentCache = true;
        [SerializeField] private ModelAsset encoderModel = null;
        [SerializeField] private ModelAsset decoderModel = null;
        [SerializeField] private ModelAsset decoderWithPastModel = null;

        private IDisposable backendLease;
        private ITranslationEngine translationEngine;

        public bool IsSupported => true;
        public bool IsReady => translationEngine != null;
        public string LastStatus => lastStatus;

        public void SetSceneReferences(
            PhraseLayerDemoBehaviour demoBehaviour,
            ModelAsset encoder,
            ModelAsset decoder,
            ModelAsset decoderWithPast)
        {
            demo = demoBehaviour ?? throw new ArgumentNullException(nameof(demoBehaviour));
            encoderModel = encoder ?? throw new ArgumentNullException(nameof(encoder));
            decoderModel = decoder ?? throw new ArgumentNullException(nameof(decoder));
            decoderWithPastModel = decoderWithPast ?? throw new ArgumentNullException(nameof(decoderWithPast));
        }

        public void SetTokenizerResourceRoot(string resourceRoot)
        {
            if (string.IsNullOrWhiteSpace(resourceRoot))
                throw new ArgumentException("Tokenizer resource root must not be empty.", nameof(resourceRoot));
            tokenizerResourceRoot = resourceRoot;
        }

        public void SetGenerationLimits(int sourceTokens, int targetTokens)
        {
            var options = OpusMtEnJaGenerationPolicy.CreateGreedyParityOptions(sourceTokens, targetTokens);
            maximumSourceTokens = options.MaximumSourceTokens;
            maximumTargetTokens = options.MaximumTargetTokens;
        }

        public void SetDeviceResidentCache(bool enabled)
        {
            useDeviceResidentCache = enabled;
        }

        private void Awake()
        {
            try
            {
                Initialize();
            }
            catch (Exception exception)
            {
                backendLease?.Dispose();
                backendLease = null;
                translationEngine = null;
                lastStatus = exception.GetType().Name + ": " + exception.Message;
                if (demo != null)
                    demo.enabled = false;
                Debug.LogException(exception, this);
            }
        }

        public void Initialize()
        {
            if (translationEngine != null)
                return;
            if (demo == null)
                throw new InvalidOperationException("Marian translation bootstrap requires a PhraseLayerDemoBehaviour reference.");
            if (encoderModel == null || decoderModel == null || decoderWithPastModel == null)
            {
                throw new InvalidOperationException(
                    "Marian translation bootstrap requires encoder_model, decoder_model, and decoder_with_past ModelAssets.");
            }
            if (string.IsNullOrWhiteSpace(tokenizerResourceRoot))
                throw new InvalidOperationException("Marian tokenizer resource root is empty.");

            if (!UnityManagedMarianTokenizerLoader.TryCreateFromResources(
                    tokenizerResourceRoot,
                    out var tokenizer,
                    out var tokenizerError))
            {
                throw new InvalidOperationException("Marian tokenizer initialization failed: " + tokenizerError);
            }

            var options = OpusMtEnJaGenerationPolicy.CreateGreedyParityOptions(
                maximumSourceTokens,
                maximumTargetTokens);

            ISeq2SeqGenerationBackend backend;
            if (useDeviceResidentCache)
            {
                backend = new UnityMarianDeviceResidentGenerationBackend(
                    encoderModel,
                    decoderModel,
                    decoderWithPastModel,
                    BackendType.GPUCompute);
            }
            else
            {
                backend = new UnityMarianSeq2SeqGenerationBackend(
                    encoderModel,
                    decoderModel,
                    decoderWithPastModel,
                    BackendType.GPUCompute);
            }

            var disposableBackend = backend as IDisposable;
            try
            {
                var generationModel = OpusMtEnJaGenerationPolicy.CreateGreedyModel(backend);
                var runtime = new OfflineSeq2SeqTranslationRuntime(tokenizer, generationModel, options);
                var engine = new OfflineTranslationEngine(runtime);
                demo.SetTranslationEngine(engine);

                backendLease = disposableBackend;
                translationEngine = engine;
                lastStatus = string.Format(
                    "Marian offline translation ready: {0}; cache={1}; source<={2}; target<={3}; beam=1.",
                    tokenizerResourceRoot,
                    useDeviceResidentCache ? "device-resident-experiment" : "cpu-clone-baseline",
                    maximumSourceTokens,
                    maximumTargetTokens);
                Debug.Log(lastStatus, this);
            }
            catch
            {
                disposableBackend?.Dispose();
                throw;
            }
        }

        private void OnDestroy()
        {
            backendLease?.Dispose();
            backendLease = null;
            translationEngine = null;
        }
#else
        public bool IsSupported => false;
        public bool IsReady => false;
        public string LastStatus => lastStatus;

        private void Awake()
        {
            lastStatus =
                "Marian translation bootstrap disabled: expected com.unity.ai.inference in the reviewed 2.2.x range.";
            if (demo != null)
                demo.enabled = false;
            Debug.Log(lastStatus, this);
        }
#endif
    }
}
