using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Deterministic translation-only scene used to prove that the reviewed Marian ModelAssets, managed tokenizer
    /// runtime and reflection-preservation descriptor are serialized into an Android player before any Quest run.
    /// This is intentionally separate from the Read Mode hardware fixture so product translation packaging cannot be
    /// confused with camera/OCR/MRUK hardware validation.
    /// </summary>
    public static class PhraseLayerMarianProductFixtureSetup
    {
        public const string ScenePath = "Assets/Scenes/PhraseLayerMarianProductFixture.unity";
        public const int MaximumSourceTokens = 128;
        public const int MaximumTargetTokens = 128;

        [MenuItem("PhraseLayer/Marian/Create Product Translation Fixture Scene")]
        public static void CreateScene()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            AssetDatabase.Refresh();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var root = new GameObject("PhraseLayer Marian Product Fixture");
            var demo = root.AddComponent<PhraseLayerDemoBehaviour>();
            var bootstrap = root.AddComponent<UnityMarianTranslationBootstrapBehaviour>();

            var encoder = LoadRequired<ModelAsset>(PhraseLayerLocalMarianAssets.EncoderPath);
            var decoder = LoadRequired<ModelAsset>(PhraseLayerLocalMarianAssets.DecoderPath);
            var decoderWithPast = LoadRequired<ModelAsset>(PhraseLayerLocalMarianAssets.DecoderWithPastPath);

            bootstrap.SetSceneReferences(demo, encoder, decoder, decoderWithPast);
            bootstrap.SetTokenizerResourceRoot(PhraseLayerLocalMarianAssets.TokenizerResourceRoot);
            bootstrap.SetGenerationLimits(MaximumSourceTokens, MaximumTargetTokens);
            bootstrap.SetDeviceResidentCache(true);
            EditorUtility.SetDirty(bootstrap);
            EditorUtility.SetDirty(demo);

            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Scenes"));
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new IOException("Failed to save PhraseLayer Marian product fixture scene.");

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log(
                "PhraseLayer Marian product fixture scene created: offline OPUS-MT en->ja, managed SentencePiece tokenizer, " +
                "device-resident cache backend, semantic-span LanguagePipeline injection; Quest execution not performed: " +
                ScenePath);
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve com.unity.ai.inference 2.2.1 before creating the Marian product fixture scene.");
#endif
        }

        public static void CreateSceneBatch()
        {
            try
            {
                CreateScene();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        private static T LoadRequired<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException("Required Marian product fixture asset did not import as " + typeof(T).FullName + ": " + path);
            return asset;
        }
#endif
    }
}
