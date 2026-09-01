using System;
using PhraseLayer.Core.Translation;
using UnityEditor;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Editor-only gate for locally exported Marian translation assets. Source weights and generated ONNX graphs
    /// remain git-ignored. Python tooling validates the exact source revision and ONNX hashes before staging;
    /// this gate proves that real Unity imported the three graphs, loaded the managed SentencePiece runtime, and
    /// can construct the pinned Marian tokenizer before the assets are assigned to the demo scene.
    /// </summary>
    public static class PhraseLayerLocalTranslationAssets
    {
        public const string GraphRoot = "Assets/LocalTranslationAssets/Marian";
        public const string EncoderPath = GraphRoot + "/encoder_model.onnx";
        public const string DecoderPath = GraphRoot + "/decoder_model.onnx";
        public const string DecoderWithPastPath = GraphRoot + "/decoder_with_past_model.onnx";
        public const string TokenizerResourceRoot = "LocalTranslationAssets";

        [MenuItem("PhraseLayer/Marian/Verify Local Translation Assets")]
        public static void VerifyLocalAssets()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            AssetDatabase.Refresh();
            var encoder = LoadRequired<ModelAsset>(EncoderPath);
            var decoder = LoadRequired<ModelAsset>(DecoderPath);
            var decoderWithPast = LoadRequired<ModelAsset>(DecoderWithPastPath);
            var report = UnityMarianOnnxContractProbe.ValidateBundle(encoder, decoder, decoderWithPast);

            if (!UnityManagedMarianTokenizerLoader.IsRuntimeAvailable)
            {
                throw new InvalidOperationException(
                    "Managed Marian tokenizer runtime is not loaded. Stage the reviewed PhraseLayer.Tokenization.Microsoft DLL closure into Assets/LocalTokenizerRuntime and reimport Unity.");
            }
            if (!UnityManagedMarianTokenizerLoader.TryCreateFromResources(
                    TokenizerResourceRoot,
                    out ITranslationTokenizer tokenizer,
                    out var tokenizerError))
            {
                throw new InvalidOperationException("Managed Marian tokenizer verification failed: " + tokenizerError);
            }
            if (tokenizer == null)
                throw new InvalidOperationException("Managed Marian tokenizer verification returned null.");

            Debug.Log(
                "PhraseLayer local Marian translation assets PASS\n" + report +
                "\ntokenizer_resource_root=" + TokenizerResourceRoot +
                "\nNOTE: exact source/export identity is enforced by Python staging; numerical translation parity, Android IL2CPP, and Quest execution remain separate gates.");
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve the reviewed com.unity.ai.inference 2.2.x package before verifying Marian assets.");
#endif
        }

        public static void VerifyLocalAssetsBatch()
        {
            try
            {
                VerifyLocalAssets();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        [MenuItem("PhraseLayer/Marian/Assign Local Translation Assets To Demo")]
        public static void AssignLocalAssetsToDemo()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            VerifyLocalAssets();
            var encoder = LoadRequired<ModelAsset>(EncoderPath);
            var decoder = LoadRequired<ModelAsset>(DecoderPath);
            var decoderWithPast = LoadRequired<ModelAsset>(DecoderWithPastPath);
            var demo = FindSingleSceneDemo();
            var bootstrap = EnsureSingleSceneBootstrap(demo);

            Undo.RecordObject(bootstrap, "Assign PhraseLayer Local Marian Translation Assets");
            bootstrap.SetSceneReferences(demo, encoder, decoder, decoderWithPast);
            bootstrap.SetTokenizerResourceRoot(TokenizerResourceRoot);
            bootstrap.SetGenerationLimits(128, 128);
            bootstrap.SetDeviceResidentCache(true);
            EditorUtility.SetDirty(bootstrap);
            if (!UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes())
                throw new InvalidOperationException("Failed to save scene after assigning Marian translation assets.");
            AssetDatabase.SaveAssets();
            Debug.Log("Assigned verified local Marian translation assets to scene bootstrap: " + bootstrap.name, bootstrap);
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve the reviewed com.unity.ai.inference 2.2.x package before assigning Marian assets.");
#endif
        }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        private static T LoadRequired<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                var imported = AssetDatabase.LoadMainAssetAtPath(path);
                var actual = imported == null ? "missing" : imported.GetType().FullName;
                throw new InvalidOperationException(
                    "Expected " + typeof(T).FullName + " at " + path + " but Unity imported " + actual +
                    ". Export and stage the exact pinned Marian bundle before continuing.");
            }
            return asset;
        }

        private static PhraseLayerDemoBehaviour FindSingleSceneDemo()
        {
            var all = Resources.FindObjectsOfTypeAll<PhraseLayerDemoBehaviour>();
            PhraseLayerDemoBehaviour found = null;
            var count = 0;
            for (var index = 0; index < all.Length; index++)
            {
                var candidate = all[index];
                if (candidate == null || EditorUtility.IsPersistent(candidate))
                    continue;
                if (!candidate.gameObject.scene.IsValid())
                    continue;
                found = candidate;
                count++;
            }
            if (count != 1 || found == null)
                throw new InvalidOperationException("Expected exactly one PhraseLayerDemoBehaviour in loaded scenes; found " + count + ".");
            return found;
        }

        private static UnityMarianTranslationBootstrapBehaviour EnsureSingleSceneBootstrap(PhraseLayerDemoBehaviour demo)
        {
            var all = Resources.FindObjectsOfTypeAll<UnityMarianTranslationBootstrapBehaviour>();
            UnityMarianTranslationBootstrapBehaviour found = null;
            var count = 0;
            for (var index = 0; index < all.Length; index++)
            {
                var candidate = all[index];
                if (candidate == null || EditorUtility.IsPersistent(candidate))
                    continue;
                if (!candidate.gameObject.scene.IsValid())
                    continue;
                found = candidate;
                count++;
            }
            if (count > 1)
                throw new InvalidOperationException("Expected at most one UnityMarianTranslationBootstrapBehaviour in loaded scenes; found " + count + ".");
            if (found != null)
                return found;

            var created = Undo.AddComponent<UnityMarianTranslationBootstrapBehaviour>(demo.gameObject);
            if (created == null)
                throw new InvalidOperationException("Failed to create UnityMarianTranslationBootstrapBehaviour on the demo object.");
            return created;
        }
#endif
    }
}
