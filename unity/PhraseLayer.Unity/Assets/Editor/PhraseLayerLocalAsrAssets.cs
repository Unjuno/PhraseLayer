using System;
using PhraseLayer.Core.Audio;
using UnityEditor;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Editor-only gate for git-ignored Moonshine Listen Mode assets. Python staging proves byte identity before
    /// these files enter Assets; this gate proves Unity imported the four graphs as ModelAsset, validates their
    /// positional ABI through the runtime contract probe, validates the managed token decoder, and can assign the
    /// verified assets to exactly one scene bootstrap.
    /// </summary>
    public static class PhraseLayerLocalAsrAssets
    {
        public const string GraphRoot = "Assets/LocalAsrAssets/MoonshineV1Tiny";
        public const string PreprocessPath = GraphRoot + "/preprocess.onnx";
        public const string EncoderPath = GraphRoot + "/encode.onnx";
        public const string UncachedDecoderPath = GraphRoot + "/uncached_decode.onnx";
        public const string CachedDecoderPath = GraphRoot + "/cached_decode.onnx";
        public const string TokenDecoderPath = "Assets/Resources/LocalAsrAssets/moonshine-tiny.tokens.bytes";

        [MenuItem("PhraseLayer/Moonshine/Verify Local ASR Assets")]
        public static void VerifyLocalAssets()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            AssetDatabase.Refresh();
            var preprocess = LoadRequired<ModelAsset>(PreprocessPath);
            var encoder = LoadRequired<ModelAsset>(EncoderPath);
            var uncached = LoadRequired<ModelAsset>(UncachedDecoderPath);
            var cached = LoadRequired<ModelAsset>(CachedDecoderPath);
            var tokens = LoadRequired<TextAsset>(TokenDecoderPath);

            var graphReport = UnityMoonshineOnnxContractProbe.ValidateBundle(
                preprocess,
                encoder,
                uncached,
                cached);
            var tokenDecoder = new MoonshineBinaryTokenDecoder(tokens.bytes);
            if (tokenDecoder.TokenCount != MoonshineTinyAsrContract.VocabularySize)
                throw new InvalidOperationException("Moonshine token decoder vocabulary drift after Unity import.");

            Debug.Log(
                "PhraseLayer local Moonshine ASR assets PASS\n" + graphReport +
                "\ntoken_count=" + tokenDecoder.TokenCount +
                "\nNOTE: byte identity is enforced by prepare_unity_moonshine_* before import; " +
                "numerical transcript parity and Quest execution remain separate gates.");
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve the reviewed com.unity.ai.inference 2.2.x package before verifying Moonshine assets.");
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

        [MenuItem("PhraseLayer/Moonshine/Assign Local ASR Assets To Scene Bootstrap")]
        public static void AssignLocalAssetsToSceneBootstrap()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            AssetDatabase.Refresh();
            var preprocess = LoadRequired<ModelAsset>(PreprocessPath);
            var encoder = LoadRequired<ModelAsset>(EncoderPath);
            var uncached = LoadRequired<ModelAsset>(UncachedDecoderPath);
            var cached = LoadRequired<ModelAsset>(CachedDecoderPath);
            var tokens = LoadRequired<TextAsset>(TokenDecoderPath);

            UnityMoonshineOnnxContractProbe.ValidateBundle(preprocess, encoder, uncached, cached);
            _ = new MoonshineBinaryTokenDecoder(tokens.bytes);

            var bootstrap = FindSingleSceneBootstrap();
            Undo.RecordObject(bootstrap, "Assign PhraseLayer Local Moonshine ASR Assets");
            var serialized = new SerializedObject(bootstrap);
            RequireProperty(serialized, "preprocessModel").objectReferenceValue = preprocess;
            RequireProperty(serialized, "encoderModel").objectReferenceValue = encoder;
            RequireProperty(serialized, "uncachedDecoderModel").objectReferenceValue = uncached;
            RequireProperty(serialized, "cachedDecoderModel").objectReferenceValue = cached;
            RequireProperty(serialized, "tokenDecoderAsset").objectReferenceValue = tokens;
            var changed = serialized.ApplyModifiedProperties();

            if (changed)
            {
                EditorUtility.SetDirty(bootstrap);
                AssetDatabase.SaveAssets();
                Debug.Log("Assigned verified local Moonshine ASR assets to scene bootstrap: " + bootstrap.name, bootstrap);
            }
            else
            {
                Debug.Log("Verified local Moonshine ASR assets were already assigned to scene bootstrap: " + bootstrap.name, bootstrap);
            }
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve the reviewed com.unity.ai.inference 2.2.x package before assigning Moonshine assets.");
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
                    ". Stage exact-revision Moonshine assets and resolve Unity import errors before continuing.");
            }
            return asset;
        }

        private static UnityMoonshineAsrBootstrapBehaviour FindSingleSceneBootstrap()
        {
            var all = Resources.FindObjectsOfTypeAll<UnityMoonshineAsrBootstrapBehaviour>();
            UnityMoonshineAsrBootstrapBehaviour found = null;
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
                throw new InvalidOperationException(
                    "Expected exactly one UnityMoonshineAsrBootstrapBehaviour in loaded scenes; found " + count + ".");
            return found;
        }

        private static SerializedProperty RequireProperty(SerializedObject serialized, string name)
        {
            var property = serialized.FindProperty(name);
            if (property == null)
                throw new InvalidOperationException("UnityMoonshineAsrBootstrapBehaviour serialized field missing: " + name);
            return property;
        }
#endif
    }
}
