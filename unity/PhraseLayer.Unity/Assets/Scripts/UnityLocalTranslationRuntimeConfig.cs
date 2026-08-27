using System;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Git-ignored Resources asset carrying a previously verified local OPUS-MT bootstrap bundle into a Player build.
    /// It contains Unity object references only; PhraseLayer does not download model weights or provide a remote
    /// translation fallback at runtime.
    /// </summary>
    public sealed class UnityLocalTranslationRuntimeConfig : ScriptableObject
    {
        public const string ResourcesName = "PhraseLayerLocalTranslationRuntimeConfig";

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        [SerializeField] private TextAsset stagingManifest = default(TextAsset);
        [SerializeField] private TextAsset managedTokenizerManifest = default(TextAsset);
        [SerializeField] private TextAsset tokenizerFixtureManifest = default(TextAsset);
        [SerializeField] private ModelAsset encoderModel = default(ModelAsset);
        [SerializeField] private ModelAsset decoderModel = default(ModelAsset);

        public bool IsConfigured =>
            stagingManifest != null &&
            managedTokenizerManifest != null &&
            tokenizerFixtureManifest != null &&
            encoderModel != null &&
            decoderModel != null;

        public string Status => IsConfigured
            ? "Local OPUS-MT runtime config is ready."
            : "Local OPUS-MT runtime config is incomplete.";

        public void ConfigureRuntime(
            UnityLocalTranslationAssetGateBehaviour assetGate,
            UnityLocalTranslationBootstrapBehaviour bootstrap,
            QuestReadAssistanceDebugBehaviour readAssistance)
        {
            if (assetGate == null) throw new ArgumentNullException(nameof(assetGate));
            if (bootstrap == null) throw new ArgumentNullException(nameof(bootstrap));
            if (readAssistance == null) throw new ArgumentNullException(nameof(readAssistance));
            if (!IsConfigured)
                throw new InvalidOperationException("Cannot configure local translation from an incomplete runtime config.");

            assetGate.Configure(stagingManifest, true);
            bootstrap.Configure(
                assetGate,
                readAssistance,
                managedTokenizerManifest,
                tokenizerFixtureManifest,
                encoderModel,
                decoderModel,
                true);
        }
#else
        public bool IsConfigured => false;
        public string Status =>
            "Local OPUS-MT runtime config is unsupported because the reviewed Unity Inference 2.2.x gate is inactive.";

        public void ConfigureRuntime(
            UnityLocalTranslationAssetGateBehaviour assetGate,
            UnityLocalTranslationBootstrapBehaviour bootstrap,
            QuestReadAssistanceDebugBehaviour readAssistance)
        {
            throw new NotSupportedException(Status);
        }
#endif
    }
}
