using System;
using System.IO;
using PhraseLayer.Core.Translation;
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
    /// Creates a deterministic local Read-MVP scene and wires the already-reviewed runtime components.
    /// PP-OCR assets are required because Read cannot operate without real text input. Local OPUS-MT is optional
    /// during development: if its complete hash-locked bootstrap bundle is present it is wired automatically;
    /// otherwise the Read debug component keeps its explicit tiny dictionary fallback.
    ///
    /// This menu is local-development tooling only. UBA does not invoke it and git-ignored model weights are never
    /// manufactured or downloaded by the Editor script.
    /// </summary>
    public static class PhraseLayerReadMvpSceneSetup
    {
        public const string ScenePath = "Assets/Scenes/PhraseLayerReadMvp.unity";

        [MenuItem("PhraseLayer/Read MVP/Create or Reset Local Read Scene")]
        public static void CreateOrResetLocalReadScene()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            AssetDatabase.Refresh();
            RequireLocalOcrAssets();
            PhraseLayerLocalOnlyBuildGuard.ApplyLocalOnlyAndroidDefaults();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var root = new GameObject("PhraseLayer Read MVP");

            var cameraBridge = root.AddComponent<MetaPassthroughCameraBridge>();
            var presenter = root.AddComponent<OcrViewportDebugBehaviour>();
            var runtimeDriver = root.AddComponent<OcrDebugRuntimeBehaviour>();
            var ocrBootstrap = root.AddComponent<UnityPaddleOcrBootstrapBehaviour>();
            var learnerProfile = root.AddComponent<UnityLearnerProfileBehaviour>();
            var readAssistance = root.AddComponent<QuestReadAssistanceDebugBehaviour>();

            AssignReference(runtimeDriver, "cameraBridge", cameraBridge);
            AssignReference(runtimeDriver, "presenter", presenter);
            AssignReference(ocrBootstrap, "runtimeDriver", runtimeDriver);
            AssignReference(readAssistance, "ocrPresenter", presenter);
            AssignReference(readAssistance, "learnerProfile", learnerProfile);

            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Scenes"));
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new IOException("Failed to save PhraseLayer Read MVP scene.");

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            PhraseLayerLocalOcrAssets.AssignLocalAssetsToSceneBootstrap();

            var translationWired = TryWireLocalTranslation(root, readAssistance);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new IOException("Failed to save PhraseLayer Read MVP scene after local asset assignment.");

            AssetDatabase.SaveAssets();
            Debug.Log(
                "PhraseLayer Read MVP scene ready: " + ScenePath +
                " OCR=local-PP-OCR translation=" + (translationWired ? "local-OPUS-MT" : "debug-dictionary"));
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve com.unity.ai.inference 2.2.x before creating the local Read MVP scene.");
#endif
        }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        private static bool TryWireLocalTranslation(GameObject root, QuestReadAssistanceDebugBehaviour readAssistance)
        {
            var manifestPath = PhraseLayerLocalTranslationAssets.ManifestAssetPath;
            if (AssetDatabase.LoadAssetAtPath<TextAsset>(manifestPath) == null)
            {
                Debug.LogWarning(
                    "Local OPUS-MT staging manifest is absent. Read MVP will use the explicit debug dictionary until local translation assets are staged.");
                return false;
            }

            PhraseLayerLocalTranslationAssets.VerifyAndPrepare();
            var stagingManifest = LoadRequired<TextAsset>(manifestPath);
            var staged = UnityLocalTranslationManifest.ParseManifest(stagingManifest);
            var runtime = LocalTranslationStagingContract.ValidateAndResolve(staged);
            var generated = LocalTranslationStagingContract.ValidateAndResolveBootstrapArtifacts(staged);

            var encoder = LoadRequired<ModelAsset>(
                PhraseLayerLocalTranslationAssets.RootAssetPath + "/" + runtime.Encoder.Path);
            var decoder = LoadRequired<ModelAsset>(
                PhraseLayerLocalTranslationAssets.RootAssetPath + "/" + runtime.Decoder.Path);
            var managedTokenizer = LoadRequired<TextAsset>(
                PhraseLayerLocalTranslationAssets.RootAssetPath + "/" + generated.ManagedTokenizerManifest.Path);
            var tokenizerFixtures = LoadRequired<TextAsset>(
                PhraseLayerLocalTranslationAssets.RootAssetPath + "/" + generated.TokenizerFixtureManifest.Path);

            var tokenizer = ManagedSentencePieceManifest.ParseTokenizer(managedTokenizer.text);
            var fixtureSet = TranslationTokenizerFixtureManifest.Parse(tokenizerFixtures.text);
            var parity = ParityVerifiedTranslationTokenizer.Verify(tokenizer, fixtureSet);
            var modelReport = UnityOpusMtModelProbe.ValidateAndBuildReport(encoder, decoder);

            var assetGate = root.AddComponent<UnityLocalTranslationAssetGateBehaviour>();
            var bootstrap = root.AddComponent<UnityLocalTranslationBootstrapBehaviour>();
            AssignReference(assetGate, "stagingManifest", stagingManifest);
            AssignReference(bootstrap, "assetGate", assetGate);
            AssignReference(bootstrap, "readAssistance", readAssistance);
            AssignReference(bootstrap, "managedTokenizerManifest", managedTokenizer);
            AssignReference(bootstrap, "tokenizerFixtureManifest", tokenizerFixtures);
            AssignReference(bootstrap, "encoderModel", encoder);
            AssignReference(bootstrap, "decoderModel", decoder);

            assetGate.ValidateBootstrapAssets(managedTokenizer, tokenizerFixtures);
            Debug.Log(
                "Local OPUS-MT scene wiring PASS: " + parity.ParityReport + " | " + modelReport,
                bootstrap);
            return true;
        }

        private static void RequireLocalOcrAssets()
        {
            foreach (var path in new[]
            {
                PhraseLayerLocalOcrAssets.DetectorPath,
                PhraseLayerLocalOcrAssets.RecognizerPath,
                PhraseLayerLocalOcrAssets.DictionaryPath,
                PhraseLayerLocalOcrAssets.DictionaryManifestPath,
            })
            {
                if (AssetDatabase.LoadMainAssetAtPath(path) == null)
                {
                    throw new InvalidOperationException(
                        "Read MVP requires staged PP-OCR assets. Missing: " + path +
                        ". Run tools/prepare_unity_ocr_assets.py before creating the local Read scene.");
                }
            }
        }

        private static T LoadRequired<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                var imported = AssetDatabase.LoadMainAssetAtPath(path);
                var actual = imported == null ? "missing" : imported.GetType().FullName;
                throw new InvalidOperationException(
                    "Expected " + typeof(T).FullName + " at " + path + " but Unity imported " + actual + ".");
            }
            return asset;
        }

        private static void AssignReference(UnityEngine.Object target, string fieldName, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(fieldName);
            if (property == null)
                throw new InvalidOperationException(target.GetType().Name + " serialized field missing: " + fieldName);
            property.objectReferenceValue = value;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
        }
#endif
    }
}
