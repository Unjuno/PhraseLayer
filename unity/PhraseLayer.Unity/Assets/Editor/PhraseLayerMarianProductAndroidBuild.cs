using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Pre-device Android ARM64 IL2CPP packaging gate for the reviewed offline Marian product translation stack.
    /// The generated APK is local evidence only while redistribution review is pending and must not be uploaded.
    /// Runtime translation parity is established separately by PhraseLayerLocalMarianAssets before this build.
    /// </summary>
    public static class PhraseLayerMarianProductAndroidBuild
    {
        private const string DefaultApplicationIdentifier = "com.unjuno.phraselayer.marianfixture";
        private const string BuildPathEnvironment = "PHRASELAYER_MARIAN_PRODUCT_FIXTURE_APK_PATH";
        private const string ApplicationIdentifierEnvironment = "PHRASELAYER_MARIAN_PRODUCT_FIXTURE_APPLICATION_ID";
        private const string DefaultRelativeBuildPath = "Builds/Android/PhraseLayerMarianProductFixture.apk";
        private const string MarianManifestRelativePath = "Assets/LocalTranslationAssets/PhraseLayerMarianAssets.manifest.json";
        private const string ReferenceRelativePath = "Assets/Resources/LocalTranslationAssets/marian-reference.json";
        private const string LinkerDescriptorRelativePath = "Assets/LocalTokenizerRuntime/link.xml";
        private const string TokenizerAdapterRelativePath = "Assets/LocalTokenizerRuntime/PhraseLayer.Tokenization.Microsoft.dll";
        private const string TokenizerRuntimeRelativePath = "Assets/LocalTokenizerRuntime/Microsoft.ML.Tokenizers.dll";
        private const string ProtobufRuntimeRelativePath = "Assets/LocalTokenizerRuntime/Google.Protobuf.dll";

        [MenuItem("PhraseLayer/Marian/Build Product Translation Android ARM64 IL2CPP Fixture")]
        public static void Build()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            EnsureAndroidTarget();
            ConfigureAndroidPlayer();

            PhraseLayerLocalMarianAssets.VerifyLocalAssets();
            PhraseLayerMarianProductFixtureSetup.CreateScene();
            if (!EditorSceneManager.SaveOpenScenes())
                throw new IOException("Failed to save PhraseLayer Marian product fixture scene.");
            AssetDatabase.SaveAssets();

            var marianManifest = RequireProjectFile(MarianManifestRelativePath);
            var reference = RequireProjectFile(ReferenceRelativePath);
            var linkerDescriptor = RequireProjectFile(LinkerDescriptorRelativePath);
            var tokenizerAdapter = RequireProjectFile(TokenizerAdapterRelativePath);
            var tokenizerRuntime = RequireProjectFile(TokenizerRuntimeRelativePath);
            var protobufRuntime = RequireProjectFile(ProtobufRuntimeRelativePath);

            var configuredScenes = EditorBuildSettings.scenes ?? Array.Empty<EditorBuildSettingsScene>();
            var enabledScenes = configuredScenes
                .Where(scene => scene != null && scene.enabled && !string.IsNullOrWhiteSpace(scene.path))
                .Select(scene => scene.path)
                .ToArray();
            if (enabledScenes.Length != 1 ||
                !string.Equals(enabledScenes[0], PhraseLayerMarianProductFixtureSetup.ScenePath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Marian product fixture must build exactly one deterministic translation scene; enabled scenes=" +
                    string.Join(",", enabledScenes));
            }

            var outputPath = ResolveOutputPath();
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException("Marian product fixture output path has no parent directory: " + outputPath);
            Directory.CreateDirectory(outputDirectory);

            var scenes = new[] { PhraseLayerMarianProductFixtureSetup.ScenePath };
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            if (report == null)
                throw new InvalidOperationException("Unity BuildPipeline returned no Marian product fixture report.");

            var summary = report.summary;
            WriteEvidence(
                outputPath,
                summary,
                scenes,
                marianManifest,
                reference,
                linkerDescriptor,
                tokenizerAdapter,
                tokenizerRuntime,
                protobufRuntime);
            if (summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "PhraseLayer Marian product fixture build failed: result={0}; errors={1}; warnings={2}; time={3}.",
                        summary.result,
                        summary.totalErrors,
                        summary.totalWarnings,
                        summary.totalTime));
            }
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
                throw new FileNotFoundException("Unity reported success but the Marian product fixture APK is missing or empty.", outputPath);

            Debug.Log(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "PhraseLayer Marian product Android packaging PASS: {0}; bytes={1}; backend=UnityMarianDeviceResidentGenerationBackend; " +
                    "target=Android ARM64 IL2CPP; Quest execution not performed; APK redistribution/upload forbidden while review is pending.",
                    outputPath,
                    new FileInfo(outputPath).Length));
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve com.unity.ai.inference 2.2.1 before building the Marian product fixture.");
#endif
        }

        public static void BuildBatch()
        {
            try
            {
                Build();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        private static void EnsureAndroidTarget()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
            {
                throw new InvalidOperationException(
                    "This Unity installation does not have Android build support. Install Android Build Support, SDK/NDK and OpenJDK for the pinned Unity editor.");
            }
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
                !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
            {
                throw new InvalidOperationException("Unity failed to switch the active build target to Android.");
            }
        }

        private static void ConfigureAndroidPlayer()
        {
            var namedTarget = NamedBuildTarget.Android;
            var applicationIdentifier = Environment.GetEnvironmentVariable(ApplicationIdentifierEnvironment);
            if (string.IsNullOrWhiteSpace(applicationIdentifier))
                applicationIdentifier = DefaultApplicationIdentifier;
            applicationIdentifier = applicationIdentifier.Trim();
            if (applicationIdentifier.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0)
                throw new InvalidOperationException("Marian product fixture application identifier must not contain whitespace.");

            PlayerSettings.SetApplicationIdentifier(namedTarget, applicationIdentifier);
            PlayerSettings.SetScriptingBackend(namedTarget, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;

            if (PlayerSettings.GetScriptingBackend(namedTarget) != ScriptingImplementation.IL2CPP)
                throw new InvalidOperationException("Marian product fixture scripting backend did not remain IL2CPP.");
            if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                throw new InvalidOperationException("Marian product fixture architecture did not remain ARM64-only.");
        }

        private static string ResolveOutputPath()
        {
            var configured = Environment.GetEnvironmentVariable(BuildPathEnvironment);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                configured = configured.Trim();
                return Path.GetFullPath(Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(ProjectRoot(), configured));
            }
            return Path.GetFullPath(Path.Combine(ProjectRoot(), DefaultRelativeBuildPath));
        }

        private static FileInfo RequireProjectFile(string assetRelativePath)
        {
            if (string.IsNullOrWhiteSpace(assetRelativePath) || Path.IsPathRooted(assetRelativePath))
                throw new ArgumentException("Marian product fixture evidence path must be project-relative.", nameof(assetRelativePath));

            var root = ProjectRoot();
            var path = Path.GetFullPath(Path.Combine(root, assetRelativePath));
            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Marian product fixture evidence path escaped the Unity project root: " + assetRelativePath);
            if (!File.Exists(path) || new FileInfo(path).Length <= 0)
                throw new FileNotFoundException("Required Marian product fixture asset/evidence is missing or empty.", path);
            return new FileInfo(path);
        }

        private static string ProjectRoot()
        {
            var assetsPath = Path.GetFullPath(Application.dataPath);
            var root = Path.GetDirectoryName(assetsPath);
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("Unable to derive the Unity project root from Application.dataPath: " + Application.dataPath);
            return Path.GetFullPath(root);
        }

        private static void WriteEvidence(
            string outputPath,
            BuildSummary summary,
            string[] scenes,
            FileInfo marianManifest,
            FileInfo reference,
            FileInfo linkerDescriptor,
            FileInfo tokenizerAdapter,
            FileInfo tokenizerRuntime,
            FileInfo protobufRuntime)
        {
            var outputDirectory = Path.GetDirectoryName(outputPath) ?? ProjectRoot();
            var evidencePath = Path.Combine(outputDirectory, "PhraseLayer.marian-product-fixture-build-evidence.json");
            var apkSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0L;
            var sceneJson = string.Join(",", scenes.Select(scene => "\"" + EscapeJson(scene) + "\""));
            var json = string.Format(
                CultureInfo.InvariantCulture,
                "{{\n  \"schema_version\": 1,\n  \"purpose\": \"phrase-layer-marian-product-android-fixture-build\",\n  \"unity_version\": \"{0}\",\n  \"application_identifier\": \"{1}\",\n  \"target\": \"Android\",\n  \"architecture\": \"ARM64\",\n  \"scripting_backend\": \"IL2CPP\",\n  \"translation_runtime\": \"MarianOpusMtEnJa\",\n  \"generation_backend\": \"UnityMarianDeviceResidentGenerationBackend\",\n  \"tokenizer_runtime\": \"Microsoft.ML.Tokenizers\",\n  \"model_revision\": \"{2}\",\n  \"product_translation_gate\": true,\n  \"semantic_span_pipeline\": true,\n  \"source_weight_copied_to_unity\": false,\n  \"il2cpp_reflection_preserve_required\": true,\n  \"redistribution_review\": \"pending\",\n  \"apk_upload_allowed\": false,\n  \"quest_device_execution_performed\": false,\n  \"android_runtime_execution_performed\": false,\n  \"deterministic_single_scene_build\": true,\n  \"project_paths_anchored_to_application_data_path\": true,\n  \"marian_asset_manifest_file\": \"{3}\",\n  \"marian_asset_manifest_size_bytes\": {4},\n  \"reference_fixture_file\": \"{5}\",\n  \"reference_fixture_size_bytes\": {6},\n  \"linker_descriptor_file\": \"{7}\",\n  \"linker_descriptor_size_bytes\": {8},\n  \"tokenizer_adapter_size_bytes\": {9},\n  \"tokenizer_runtime_size_bytes\": {10},\n  \"protobuf_runtime_size_bytes\": {11},\n  \"result\": \"{12}\",\n  \"total_errors\": {13},\n  \"total_warnings\": {14},\n  \"total_size_bytes_reported\": {15},\n  \"apk_size_bytes\": {16},\n  \"elapsed_seconds\": {17:F6},\n  \"scenes\": [{18}]\n}}\n",
                EscapeJson(Application.unityVersion),
                EscapeJson(PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android)),
                EscapeJson(PhraseLayerLocalMarianAssets.ExpectedRevision),
                EscapeJson(marianManifest.Name),
                marianManifest.Length,
                EscapeJson(reference.Name),
                reference.Length,
                EscapeJson(linkerDescriptor.Name),
                linkerDescriptor.Length,
                tokenizerAdapter.Length,
                tokenizerRuntime.Length,
                protobufRuntime.Length,
                EscapeJson(summary.result.ToString()),
                summary.totalErrors,
                summary.totalWarnings,
                summary.totalSize,
                apkSize,
                summary.totalTime.TotalSeconds,
                sceneJson);
            File.WriteAllText(evidencePath, json);
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }
#endif
    }
}
