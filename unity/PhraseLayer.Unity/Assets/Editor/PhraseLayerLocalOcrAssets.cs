using System;
using UnityEditor;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Editor-only bridge for the git-ignored OCR assets prepared by tools/prepare_unity_ocr_assets.py.
    /// It verifies Unity import types, probes both imported models, validates the generated dictionary manifest,
    /// and can assign the reviewed local assets to exactly one scene bootstrap without serializing model binaries.
    /// </summary>
    public static class PhraseLayerLocalOcrAssets
    {
        public const string Root = "Assets/LocalOcrAssets/PaddleOCR";
        public const string DetectorPath = Root + "/detector.onnx";
        public const string RecognizerPath = Root + "/recognizer.onnx";
        public const string DictionaryPath = Root + "/ppocr_keys.txt";
        public const string DictionaryManifestPath = Root + "/ppocr_keys.manifest.json";

        [MenuItem("PhraseLayer/PP-OCR/Verify Local Assets")]
        public static void VerifyLocalAssets()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            AssetDatabase.Refresh();

            var detector = LoadRequired<ModelAsset>(DetectorPath);
            var recognizer = LoadRequired<ModelAsset>(RecognizerPath);
            var dictionary = LoadRequired<TextAsset>(DictionaryPath);
            var manifest = LoadRequired<TextAsset>(DictionaryManifestPath);

            var dictionaryReport = UnityPaddleOcrDictionaryManifest.Validate(
                dictionary,
                manifest,
                configuredUseSpaceCharacter: true);
            var detectorReport = UnityInferenceModelProbe.BuildReport(detector);
            var recognizerReport = UnityInferenceModelProbe.BuildReport(recognizer);

            Debug.Log(
                "PhraseLayer local PP-OCR assets PASS\n" +
                dictionaryReport + "\n--- detector ---\n" + detectorReport +
                "\n--- recognizer ---\n" + recognizerReport);
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve the reviewed com.unity.ai.inference 2.2.x package before verifying local OCR assets.");
#endif
        }

        /// <summary>
        /// Batchmode entry point for a real Unity import/probe gate.
        /// Example: Unity -batchmode -projectPath unity/PhraseLayer.Unity -executeMethod PhraseLayer.Unity.Editor.PhraseLayerLocalOcrAssets.VerifyLocalAssetsBatch -quit
        /// </summary>
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

        [MenuItem("PhraseLayer/PP-OCR/Assign Local Assets To Scene Bootstrap")]
        public static void AssignLocalAssetsToSceneBootstrap()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            AssetDatabase.Refresh();

            var detector = LoadRequired<ModelAsset>(DetectorPath);
            var recognizer = LoadRequired<ModelAsset>(RecognizerPath);
            var dictionary = LoadRequired<TextAsset>(DictionaryPath);
            var manifest = LoadRequired<TextAsset>(DictionaryManifestPath);

            // Refuse to assign stale or edited local dictionary assets.
            UnityPaddleOcrDictionaryManifest.Validate(
                dictionary,
                manifest,
                configuredUseSpaceCharacter: true);

            var bootstrap = FindSingleSceneBootstrap();
            Undo.RecordObject(bootstrap, "Assign PhraseLayer Local PP-OCR Assets");

            var serialized = new SerializedObject(bootstrap);
            RequireProperty(serialized, "detectorModel").objectReferenceValue = detector;
            RequireProperty(serialized, "recognizerModel").objectReferenceValue = recognizer;
            RequireProperty(serialized, "characterDictionary").objectReferenceValue = dictionary;
            RequireProperty(serialized, "characterDictionaryManifest").objectReferenceValue = manifest;
            RequireProperty(serialized, "useSpaceCharacter").boolValue = true;
            var changed = serialized.ApplyModifiedProperties();

            if (changed)
            {
                EditorUtility.SetDirty(bootstrap);
                AssetDatabase.SaveAssets();
                Debug.Log("Assigned verified local PP-OCR assets to scene bootstrap: " + bootstrap.name, bootstrap);
            }
            else
            {
                Debug.Log("Verified local PP-OCR assets were already assigned to scene bootstrap: " + bootstrap.name, bootstrap);
            }
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve the reviewed com.unity.ai.inference 2.2.x package before assigning local OCR assets.");
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
                    ". Run tools/prepare_unity_ocr_assets.py and resolve any Unity import errors before continuing.");
            }
            return asset;
        }

        private static UnityPaddleOcrBootstrapBehaviour FindSingleSceneBootstrap()
        {
            var all = Resources.FindObjectsOfTypeAll<UnityPaddleOcrBootstrapBehaviour>();
            UnityPaddleOcrBootstrapBehaviour found = null;
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
            {
                throw new InvalidOperationException(
                    "Expected exactly one UnityPaddleOcrBootstrapBehaviour in loaded scenes; found " + count + ".");
            }
            return found;
        }

        private static SerializedProperty RequireProperty(SerializedObject serialized, string name)
        {
            var property = serialized.FindProperty(name);
            if (property == null)
                throw new InvalidOperationException("UnityPaddleOcrBootstrapBehaviour serialized field missing: " + name);
            return property;
        }
#endif
    }
}
