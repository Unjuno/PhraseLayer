using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Generates the git-ignored Resources asset that carries verified local PP-OCR references into Player builds.
    /// A completely absent local OCR bundle keeps the deterministic synthetic fallback. A partial bundle fails closed
    /// instead of producing a build whose runtime mode is ambiguous.
    /// </summary>
    public static class PhraseLayerLocalOcrRuntimeConfigBuilder
    {
        public const string RuntimeResourcesDirectory = PhraseLayerLocalOcrAssets.Root + "/Resources";
        public const string RuntimeConfigAssetPath = RuntimeResourcesDirectory + "/PhraseLayerLocalOcrRuntimeConfig.asset";

        [MenuItem("PhraseLayer/PP-OCR/Prepare Player Runtime Config")]
        public static void PrepareRuntimeConfigAsset()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            AssetDatabase.Refresh();
            PhraseLayerLocalOcrAssets.VerifyLocalAssets();

            var detector = LoadRequired<ModelAsset>(PhraseLayerLocalOcrAssets.DetectorPath);
            var recognizer = LoadRequired<ModelAsset>(PhraseLayerLocalOcrAssets.RecognizerPath);
            var dictionary = LoadRequired<TextAsset>(PhraseLayerLocalOcrAssets.DictionaryPath);
            var dictionaryManifest = LoadRequired<TextAsset>(PhraseLayerLocalOcrAssets.DictionaryManifestPath);

            UnityPaddleOcrDictionaryManifest.Validate(
                dictionary,
                dictionaryManifest,
                configuredUseSpaceCharacter: true);

            Directory.CreateDirectory(RuntimeResourcesDirectory);
            AssetDatabase.Refresh();

            var config = AssetDatabase.LoadAssetAtPath<UnityLocalOcrRuntimeConfig>(RuntimeConfigAssetPath);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<UnityLocalOcrRuntimeConfig>();
                AssetDatabase.CreateAsset(config, RuntimeConfigAssetPath);
            }

            var serialized = new SerializedObject(config);
            RequireProperty(serialized, "detectorModel").objectReferenceValue = detector;
            RequireProperty(serialized, "recognizerModel").objectReferenceValue = recognizer;
            RequireProperty(serialized, "characterDictionary").objectReferenceValue = dictionary;
            RequireProperty(serialized, "characterDictionaryManifest").objectReferenceValue = dictionaryManifest;
            RequireProperty(serialized, "useSpaceCharacter").boolValue = true;
            RequireProperty(serialized, "recognitionDropScore").floatValue = 0.5f;
            RequireProperty(serialized, "recognizerModelWidth").intValue =
                PhraseLayer.Core.Inputs.PaddleOcrV6TinyRecognitionPreprocess.DefaultModelWidth;
            RequireProperty(serialized, "detectorBackend").enumValueIndex = (int)BackendType.GPUCompute;
            RequireProperty(serialized, "recognizerBackend").enumValueIndex = (int)BackendType.GPUCompute;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (!config.IsConfigured)
                throw new InvalidOperationException("Generated local PP-OCR runtime config is incomplete after serialization.");

            Debug.Log(
                "PhraseLayer local PP-OCR Player runtime config PASS: " + RuntimeConfigAssetPath +
                ". The asset remains under the git-ignored LocalOcrAssets tree.",
                config);
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve the reviewed com.unity.ai.inference 2.2.x package before preparing the local OCR Player config.");
#endif
        }

        public static bool HasAnyStagedLocalOcrFile()
        {
            return File.Exists(PhraseLayerLocalOcrAssets.DetectorPath) ||
                   File.Exists(PhraseLayerLocalOcrAssets.RecognizerPath) ||
                   File.Exists(PhraseLayerLocalOcrAssets.DictionaryPath) ||
                   File.Exists(PhraseLayerLocalOcrAssets.DictionaryManifestPath);
        }

        public static bool HasCompleteStagedLocalOcrBundle()
        {
            return File.Exists(PhraseLayerLocalOcrAssets.DetectorPath) &&
                   File.Exists(PhraseLayerLocalOcrAssets.RecognizerPath) &&
                   File.Exists(PhraseLayerLocalOcrAssets.DictionaryPath) &&
                   File.Exists(PhraseLayerLocalOcrAssets.DictionaryManifestPath);
        }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        private static T LoadRequired<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException("Required local OCR asset is missing or imported as the wrong type: " + path);
            return asset;
        }

        private static SerializedProperty RequireProperty(SerializedObject serialized, string name)
        {
            var property = serialized.FindProperty(name);
            if (property == null)
                throw new InvalidOperationException("UnityLocalOcrRuntimeConfig serialized field missing: " + name);
            return property;
        }
#endif
    }

    public sealed class PhraseLayerLocalOcrRuntimeConfigBuildHook : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1500;

        public void OnPreprocessBuild(BuildReport report)
        {
            var hasAny = PhraseLayerLocalOcrRuntimeConfigBuilder.HasAnyStagedLocalOcrFile();
            if (!hasAny)
            {
                Debug.Log("PhraseLayer local PP-OCR assets are absent; Player will keep the synthetic OCR fallback.");
                return;
            }

            if (!PhraseLayerLocalOcrRuntimeConfigBuilder.HasCompleteStagedLocalOcrBundle())
            {
                throw new BuildFailedException(
                    "PhraseLayer found a partial local PP-OCR bundle. Stage detector.onnx, recognizer.onnx, ppocr_keys.txt, " +
                    "and ppocr_keys.manifest.json together, or remove the partial bundle before building.");
            }

            PhraseLayerLocalOcrRuntimeConfigBuilder.PrepareRuntimeConfigAsset();
        }
    }
}
