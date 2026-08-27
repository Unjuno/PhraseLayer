using System;
using System.IO;
using System.Reflection;
using PhraseLayer.Core.Translation;
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
    /// Generates the git-ignored Resources asset that carries an already verified local OPUS-MT bundle into Player
    /// builds. Absent local translation assets keep the explicit tiny debug dictionary. Any partial/stale local bundle
    /// fails before export instead of silently changing translation identity.
    /// </summary>
    public static class PhraseLayerLocalTranslationRuntimeConfigBuilder
    {
        public const string RuntimeResourcesDirectory = PhraseLayerLocalTranslationAssets.RootAssetPath + "/Resources";
        public const string RuntimeConfigAssetPath = RuntimeResourcesDirectory + "/PhraseLayerLocalTranslationRuntimeConfig.asset";

        [MenuItem("PhraseLayer/Translation/Prepare Player Runtime Config")]
        public static void PrepareRuntimeConfigAsset()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            AssetDatabase.Refresh();
            var integrityReport = PhraseLayerLocalTranslationAssets.VerifyAndPrepare();
            AssetDatabase.Refresh();

            var stagingManifest = LoadRequired<TextAsset>(PhraseLayerLocalTranslationAssets.ManifestAssetPath);
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

            Directory.CreateDirectory(RuntimeResourcesDirectory);
            AssetDatabase.Refresh();

            var config = AssetDatabase.LoadAssetAtPath<UnityLocalTranslationRuntimeConfig>(RuntimeConfigAssetPath);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<UnityLocalTranslationRuntimeConfig>();
                CreateAsset(config, RuntimeConfigAssetPath);
            }

            var serialized = new SerializedObject(config);
            RequireProperty(serialized, "stagingManifest").objectReferenceValue = stagingManifest;
            RequireProperty(serialized, "managedTokenizerManifest").objectReferenceValue = managedTokenizer;
            RequireProperty(serialized, "tokenizerFixtureManifest").objectReferenceValue = tokenizerFixtures;
            RequireProperty(serialized, "encoderModel").objectReferenceValue = encoder;
            RequireProperty(serialized, "decoderModel").objectReferenceValue = decoder;
            serialized.ApplyModifiedProperties();

            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (!config.IsConfigured)
                throw new InvalidOperationException("Generated local OPUS-MT runtime config is incomplete after serialization.");

            Debug.Log(
                "PhraseLayer local OPUS-MT Player runtime config PASS: " + RuntimeConfigAssetPath +
                " | " + integrityReport + " | " + parity.ParityReport + " | " + modelReport,
                config);
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve the reviewed com.unity.ai.inference 2.2.x package before preparing the local translation Player config.");
#endif
        }

        public static bool HasAnyStagedLocalTranslationFile()
        {
            var root = ToFullPath(PhraseLayerLocalTranslationAssets.RootAssetPath);
            if (!Directory.Exists(root))
                return false;

            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            for (var index = 0; index < files.Length; index++)
            {
                if (!files[index].EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        private static T LoadRequired<T>(string path) where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new InvalidOperationException("Required local translation asset is missing or imported as the wrong type: " + path);
            return asset;
        }

        private static SerializedProperty RequireProperty(SerializedObject serialized, string name)
        {
            var property = serialized.FindProperty(name);
            if (property == null)
                throw new InvalidOperationException("UnityLocalTranslationRuntimeConfig serialized field missing: " + name);
            return property;
        }

        private static void CreateAsset(UnityEngine.Object asset, string path)
        {
            var method = typeof(AssetDatabase).GetMethod(
                "CreateAsset",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(UnityEngine.Object), typeof(string) },
                null);
            if (method == null)
                throw new MissingMethodException(typeof(AssetDatabase).FullName, "CreateAsset(Object, string)");
            method.Invoke(null, new object[] { asset, path });
        }
#endif

        private static string ToFullPath(string assetPath)
        {
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException("Expected an Assets-relative path: " + assetPath);

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
                throw new InvalidOperationException("Cannot resolve Unity project root from Application.dataPath.");
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    public sealed class PhraseLayerLocalTranslationRuntimeConfigBuildHook : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1400;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!PhraseLayerLocalTranslationRuntimeConfigBuilder.HasAnyStagedLocalTranslationFile())
            {
                Debug.Log("PhraseLayer local OPUS-MT assets are absent; Player will keep the explicit debug dictionary fallback.");
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<TextAsset>(PhraseLayerLocalTranslationAssets.ManifestAssetPath) == null)
            {
                throw new BuildFailedException(
                    "PhraseLayer found local translation files without the hash-locked staging manifest. " +
                    "Run tools/prepare_unity_translation_assets.py from a parity-verified export, or remove the partial local bundle.");
            }

            try
            {
                PhraseLayerLocalTranslationRuntimeConfigBuilder.PrepareRuntimeConfigAsset();
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new BuildFailedException(
                    "PhraseLayer local OPUS-MT bundle failed Player pre-export validation: " + exception.Message);
            }
        }
    }
}
