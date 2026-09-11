using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Creates the full Read Mode scene with the reviewed offline Marian product translator serialized into the same
    /// root as passthrough camera/OCR/MRUK/masking/world-text. This is a host packaging integration gate only: the Quest
    /// smoke remains disabled and no camera, OCR, MRUK, translation, or hardware runtime PASS is claimed by scene setup.
    /// </summary>
    public static class PhraseLayerReadModeMarianProductFixtureSetup
    {
        public const string ScenePath = PhraseLayerEditorSetup.DemoScenePath;
        public const int MaximumSourceTokens = PhraseLayerMarianProductFixtureSetup.MaximumSourceTokens;
        public const int MaximumTargetTokens = PhraseLayerMarianProductFixtureSetup.MaximumTargetTokens;

        [MenuItem("PhraseLayer/Read Mode/Create Marian Product Packaging Scene")]
        public static void CreateScene()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            PhraseLayerLocalMarianAssets.VerifyLocalAssets();
            PhraseLayerLocalReadModeVisualAssets.StageAndCreateDemoScene(
                autoRunQuestReadModeSmoke: false,
                configureRoot: ConfigureMarianProductTranslation);
            PhraseLayerLocalOcrAssets.AssignLocalAssetsToSceneBootstrap();

            if (!EditorSceneManager.SaveOpenScenes())
                throw new IOException("Failed to save combined Read Mode + Marian product packaging scene.");
            AssetDatabase.SaveAssets();

            Debug.Log(
                "PhraseLayer combined Read Mode + Marian product scene created: PaddleOCR + captured-pose projection + " +
                "MRUK live-depth + source mask/world text + MarianOpusMtEnJa device-resident backend + managed tokenizer. " +
                "Quest smoke autorun=false; runtime execution not performed by scene setup: " + ScenePath);
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve com.unity.ai.inference 2.2.1 before creating the combined Read Mode + Marian product scene.");
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
        private static void ConfigureMarianProductTranslation(GameObject root, PhraseLayerDemoBehaviour demo)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));
            if (demo == null)
                throw new ArgumentNullException(nameof(demo));

            var bootstrap = root.AddComponent<UnityMarianTranslationBootstrapBehaviour>();
            var encoder = LoadRequired<ModelAsset>(PhraseLayerLocalMarianAssets.EncoderPath);
            var decoder = LoadRequired<ModelAsset>(PhraseLayerLocalMarianAssets.DecoderPath);
            var decoderWithPast = LoadRequired<ModelAsset>(PhraseLayerLocalMarianAssets.DecoderWithPastPath);

            demo.SetAutoRunOnStart(true);
            bootstrap.SetSceneReferences(demo, encoder, decoder, decoderWithPast);
            bootstrap.SetTokenizerResourceRoot(PhraseLayerLocalMarianAssets.TokenizerResourceRoot);
            bootstrap.SetGenerationLimits(MaximumSourceTokens, MaximumTargetTokens);
            bootstrap.SetDeviceResidentCache(true);
            EditorUtility.SetDirty(bootstrap);
            EditorUtility.SetDirty(demo);
        }

        private static T LoadRequired<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                throw new InvalidOperationException(
                    "Required combined Read Mode + Marian product asset did not import as " +
                    typeof(T).FullName + ": " + path);
            }
            return asset;
        }
#endif
    }
}
