using System;
using PhraseLayer.Core.Audio;
using PhraseLayer.Core.Inputs;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Explicit scene bootstrap for PhraseLayer's offline English Listen Mode ASR stack.
    ///
    /// The four Moonshine v1 ModelAssets must come from the separately pinned/staged reference bundle, while
    /// tokenDecoderAsset is generated from the separately pinned moonshine-tiny tokenizer.json. Those two source
    /// identities remain intentionally distinct until numerical parity is proven. Missing or incompatible assets
    /// fail loudly; this component never substitutes FakeAsrEngine or a network recognizer.
    /// </summary>
    public sealed class UnityMoonshineAsrBootstrapBehaviour : MonoBehaviour
    {
        [SerializeField] private string lastStatus = "Moonshine ASR runtime not initialized.";

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        [SerializeField] private ModelAsset preprocessModel = null;
        [SerializeField] private ModelAsset encoderModel = null;
        [SerializeField] private ModelAsset uncachedDecoderModel = null;
        [SerializeField] private ModelAsset cachedDecoderModel = null;
        [SerializeField] private TextAsset tokenDecoderAsset = null;
        [SerializeField] private string tokenDecoderResourcePath = "LocalAsrAssets/moonshine-tiny.tokens";
        [SerializeField] private int maximumGenerationLength = MoonshineTinyAsrContract.MaximumGenerationLength;
        [SerializeField] private bool useGpuCompute = true;

        private IDisposable backendLease;
        private IAsrEngine asrEngine;

        public bool IsSupported => true;
        public bool IsReady => asrEngine != null;
        public string LastStatus => lastStatus;
        public IAsrEngine AsrEngine => asrEngine ?? throw new InvalidOperationException("Moonshine ASR runtime is not initialized.");

        public void SetSceneReferences(
            ModelAsset preprocess,
            ModelAsset encoder,
            ModelAsset uncachedDecoder,
            ModelAsset cachedDecoder,
            TextAsset tokenDecoder)
        {
            preprocessModel = preprocess ?? throw new ArgumentNullException(nameof(preprocess));
            encoderModel = encoder ?? throw new ArgumentNullException(nameof(encoder));
            uncachedDecoderModel = uncachedDecoder ?? throw new ArgumentNullException(nameof(uncachedDecoder));
            cachedDecoderModel = cachedDecoder ?? throw new ArgumentNullException(nameof(cachedDecoder));
            tokenDecoderAsset = tokenDecoder ?? throw new ArgumentNullException(nameof(tokenDecoder));
        }

        public void SetTokenDecoderResourcePath(string resourcePath)
        {
            if (string.IsNullOrWhiteSpace(resourcePath))
                throw new ArgumentException("Moonshine token decoder resource path must not be empty.", nameof(resourcePath));
            tokenDecoderResourcePath = resourcePath;
        }

        public void SetGenerationLimit(int maximumTokens)
        {
            if (maximumTokens <= 0 || maximumTokens > MoonshineTinyAsrContract.MaximumGenerationLength)
                throw new ArgumentOutOfRangeException(nameof(maximumTokens));
            maximumGenerationLength = maximumTokens;
        }

        public void SetGpuCompute(bool enabled)
        {
            useGpuCompute = enabled;
        }

        private void Awake()
        {
            try
            {
                Initialize();
            }
            catch (Exception exception)
            {
                DisposeRuntime();
                lastStatus = exception.GetType().Name + ": " + exception.Message;
                enabled = false;
                Debug.LogException(exception, this);
            }
        }

        public void Initialize()
        {
            if (asrEngine != null)
                return;
            if (preprocessModel == null || encoderModel == null || uncachedDecoderModel == null || cachedDecoderModel == null)
            {
                throw new InvalidOperationException(
                    "Moonshine ASR bootstrap requires preprocess, encoder, uncached decoder, and cached decoder ModelAssets.");
            }

            var decoderAsset = tokenDecoderAsset;
            if (decoderAsset == null)
            {
                if (string.IsNullOrWhiteSpace(tokenDecoderResourcePath))
                    throw new InvalidOperationException("Moonshine token decoder resource path is empty.");
                decoderAsset = Resources.Load<TextAsset>(tokenDecoderResourcePath);
            }
            if (decoderAsset == null || decoderAsset.bytes == null || decoderAsset.bytes.Length == 0)
            {
                throw new InvalidOperationException(
                    "Moonshine ASR bootstrap requires the generated token decoder binary asset at Resources/" +
                    tokenDecoderResourcePath + ".bytes or an explicit TextAsset reference.");
            }
            if (maximumGenerationLength <= 0 || maximumGenerationLength > MoonshineTinyAsrContract.MaximumGenerationLength)
                throw new InvalidOperationException("Moonshine ASR maximum generation length is outside the reviewed contract.");

            var decoder = new MoonshineBinaryTokenDecoder(decoderAsset.bytes);
            var backend = new UnityMoonshineV1GenerationBackend(
                preprocessModel,
                encoderModel,
                uncachedDecoderModel,
                cachedDecoderModel,
                useGpuCompute ? BackendType.GPUCompute : BackendType.CPU);
            var disposableBackend = backend as IDisposable;

            try
            {
                var runtime = new MoonshineGreedyAsrRuntime(backend, decoder, maximumGenerationLength);
                var engine = new OfflineAsrEngine(runtime);

                backendLease = disposableBackend;
                asrEngine = engine;
                lastStatus = string.Format(
                    "Moonshine offline ASR ready: 16 kHz; greedy; max={0}; backend={1}; deployment=v1-reference-four-graph; tokens={2}.",
                    maximumGenerationLength,
                    useGpuCompute ? "GPUCompute" : "CPU",
                    tokenDecoderAsset != null ? "scene-asset" : tokenDecoderResourcePath);
                Debug.Log(lastStatus, this);
            }
            catch
            {
                disposableBackend?.Dispose();
                throw;
            }
        }

        public bool TryGetAsrEngine(out IAsrEngine engine)
        {
            engine = asrEngine;
            return engine != null;
        }

        private void OnDestroy()
        {
            DisposeRuntime();
        }

        private void DisposeRuntime()
        {
            backendLease?.Dispose();
            backendLease = null;
            asrEngine = null;
        }
#else
        public bool IsSupported => false;
        public bool IsReady => false;
        public string LastStatus => lastStatus;

        private void Awake()
        {
            lastStatus =
                "Moonshine ASR bootstrap disabled: expected com.unity.ai.inference in the reviewed 2.2.x range.";
            enabled = false;
            Debug.Log(lastStatus);
        }
#endif
    }
}
