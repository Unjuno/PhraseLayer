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
    /// Host-only Android ARM64 IL2CPP packaging gate for the complete offline Read Mode product stack.
    /// The deterministic scene serializes PaddleOCR, Meta/MRUK placement, masking/world text and the reviewed Marian
    /// English-to-Japanese translator into one APK. Packaging does not execute camera/OCR/MRUK/translation or claim
    /// Quest hardware success. The APK remains local-only while OCR/translation redistribution review is pending.
    /// </summary>
    public static class PhraseLayerReadModeMarianProductAndroidBuild
    {
        private const string DefaultApplicationIdentifier = "com.unjuno.phraselayer.readmodemarianfixture";
        private const string BuildPathEnvironment = "PHRASELAYER_READ_MODE_MARIAN_PRODUCT_APK_PATH";
        private const string ApplicationIdentifierEnvironment = "PHRASELAYER_READ_MODE_MARIAN_PRODUCT_APPLICATION_ID";
        private const string DefaultRelativeBuildPath = "Builds/Android/PhraseLayerReadModeMarianProductFixture.apk";
        private const string VisualEvidenceRelativePath = "Assets/LocalReadModeAssets/read-mode-visual-assets.json";
        private const string OcrManifestRelativePath = "Assets/LocalOcrAssets/PaddleOCR/PhraseLayerOcrAssets.manifest.json";
        private const string MarianManifestRelativePath = "Assets/LocalTranslationAssets/PhraseLayerMarianAssets.manifest.json";
        private const string ReferenceRelativePath = "Assets/Resources/LocalTranslationAssets/marian-reference.json";
        private const string LinkerDescriptorRelativePath = "Assets/LocalTokenizerRuntime/link.xml";
        private const string TokenizerAdapterRelativePath = "Assets/LocalTokenizerRuntime/PhraseLayer.Tokenization.Microsoft.dll";
        private const string TokenizerRuntimeRelativePath = "Assets/LocalTokenizerRuntime/Microsoft.ML.Tokenizers.dll";
        private const string ProtobufRuntimeRelativePath = "Assets/LocalTokenizerRuntime/Google.Protobuf.dll";

        [MenuItem("PhraseLayer/Read Mode/Build Marian Product Android ARM64 IL2CPP Fixture")]
        public static void Build()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            EnsureAndroidTarget();
            ConfigureAndroidPlayer();

            PhraseLayerReadModeMarianProductFixtureSetup.CreateScene();
            if (!EditorSceneManager.SaveOpenScenes())
                throw new IOException("Failed to save combined Read Mode + Marian product packaging scene.");
            AssetDatabase.SaveAssets();

            var visualEvidence = RequireProjectFile(VisualEvidenceRelativePath);
            var ocrManifest = RequireProjectFile(OcrManifestRelativePath);
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
                !string.Equals(enabledScenes[0], PhraseLayerReadModeMarianProductFixtureSetup.ScenePath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Combined Read Mode + Marian product fixture must build exactly the deterministic demo scene; enabled scenes=" +
                    string.Join(",", enabledScenes));
            }

            var outputPath = ResolveOutputPath();
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException("Combined product fixture output path has no parent directory: " + outputPath);
            Directory.CreateDirectory(outputDirectory);

            var scenes = new[] { PhraseLayerReadModeMarianProductFixtureSetup.ScenePath };
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
                throw new InvalidOperationException("Unity BuildPipeline returned no combined Read Mode + Marian product report.");

            var summary = report.summary;
            WriteEvidence(
                outputPath,
                summary,
                scenes,
                visualEvidence,
                ocrManifest,
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
                        "PhraseLayer combined Read Mode + Marian product build failed: result={0}; errors={1}; warnings={2}; time={3}.",
                        summary.result,
                        summary.totalErrors,
                        summary.totalWarnings,
                        summary.totalTime));
            }
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
                throw new FileNotFoundException("Unity reported success but the combined product APK is missing or empty.", outputPath);

            Debug.Log(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "PhraseLayer combined Read Mode + Marian product packaging PASS: {0}; bytes={1}; " +
                    "runtime=PaddleOCR+CapturedCameraPose+MRUKEnvironmentRaycast+MarianOpusMtEnJa; " +
                    "target=Android ARM64 IL2CPP; product_translation_gate=true; Quest/runtime execution not performed; " +
                    "APK upload forbidden while redistribution review is pending.",
                    outputPath,
                    new FileInfo(outputPath).Length));
#else
            throw new InvalidOperationException(
                "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve com.unity.ai.inference 2.2.1 before building the combined Read Mode + Marian product fixture.");
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
                throw new InvalidOperationException("Combined product application identifier must not contain whitespace.");

            PlayerSettings.SetApplicationIdentifier(namedTarget, applicationIdentifier);
            PlayerSettings.SetScriptingBackend(namedTarget, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;

            if (PlayerSettings.GetScriptingBackend(namedTarget) != ScriptingImplementation.IL2CPP)
                throw new InvalidOperationException("Combined product scripting backend did not remain IL2CPP.");
            if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                throw new InvalidOperationException("Combined product architecture did not remain ARM64-only.");
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
                throw new ArgumentException("Combined product evidence path must be project-relative.", nameof(assetRelativePath));

            var root = ProjectRoot();
            var path = Path.GetFullPath(Path.Combine(root, assetRelativePath));
            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Combined product evidence path escaped the Unity project root: " + assetRelativePath);
            if (!File.Exists(path) || new FileInfo(path).Length <= 0)
                throw new FileNotFoundException("Required combined product asset/evidence is missing or empty.", path);
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
            FileInfo visualEvidence,
            FileInfo ocrManifest,
            FileInfo marianManifest,
            FileInfo reference,
            FileInfo linkerDescriptor,
            FileInfo tokenizerAdapter,
            FileInfo tokenizerRuntime,
            FileInfo protobufRuntime)
        {
            var outputDirectory = Path.GetDirectoryName(outputPath) ?? ProjectRoot();
            var evidencePath = Path.Combine(outputDirectory, "PhraseLayer.read-mode-marian-product-build-evidence.json");
            var apkSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0L;
            var sceneJson = string.Join(",", scenes.Select(scene => "\"" + EscapeJson(scene) + "\""));
            var json = string.Format(
                CultureInfo.InvariantCulture,
                "{{\n  \"schema_version\": 1,\n  \"purpose\": \"phrase-layer-read-mode-marian-product-android-fixture-build\",\n  \"unity_version\": \"{0}\",\n  \"application_identifier\": \"{1}\",\n  \"target\": \"Android\",\n  \"architecture\": \"ARM64\",\n  \"scripting_backend\": \"IL2CPP\",\n  \"ocr_runtime\": \"PaddleOCR\",\n  \"surface_runtime\": \"MRUKEnvironmentRaycast\",\n  \"translation_runtime\": \"MarianOpusMtEnJa\",\n  \"generation_backend\": \"UnityMarianDeviceResidentGenerationBackend\",\n  \"tokenizer_runtime\": \"Microsoft.ML.Tokenizers\",\n  \"model_revision\": \"{2}\",\n  \"product_translation_gate\": true,\n  \"semantic_span_pipeline\": true,\n  \"combined_single_scene_packaging\": true,\n  \"source_weight_copied_to_unity\": false,\n  \"il2cpp_reflection_preserve_required\": true,\n  \"camera_timestamp_source\": \"MetaPassthroughCameraAccess.Timestamp\",\n  \"camera_pose_source\": \"MetaPassthroughCameraAccess.GetCameraPose\",\n  \"captured_pose_projection_required\": true,\n  \"camera_timestamp_pose_binding_implemented\": true,\n  \"camera_pixel_pose_sync_verified\": false,\n  \"quest_read_mode_smoke_autorun\": false,\n  \"quest_device_execution_performed\": false,\n  \"android_runtime_execution_performed\": false,\n  \"deterministic_single_scene_build\": true,\n  \"project_paths_anchored_to_application_data_path\": true,\n  \"source_mask_shader\": \"PhraseLayer/SourceMask\",\n  \"ocr_redistribution_review\": \"pending\",\n  \"translation_redistribution_review\": \"pending\",\n  \"apk_upload_allowed\": false,\n  \"visual_asset_evidence_file\": \"{3}\",\n  \"visual_asset_evidence_size_bytes\": {4},\n  \"ocr_asset_manifest_file\": \"{5}\",\n  \"ocr_asset_manifest_size_bytes\": {6},\n  \"marian_asset_manifest_file\": \"{7}\",\n  \"marian_asset_manifest_size_bytes\": {8},\n  \"reference_fixture_file\": \"{9}\",\n  \"reference_fixture_size_bytes\": {10},\n  \"linker_descriptor_file\": \"{11}\",\n  \"linker_descriptor_size_bytes\": {12},\n  \"tokenizer_adapter_size_bytes\": {13},\n  \"tokenizer_runtime_size_bytes\": {14},\n  \"protobuf_runtime_size_bytes\": {15},\n  \"result\": \"{16}\",\n  \"total_errors\": {17},\n  \"total_warnings\": {18},\n  \"total_size_bytes_reported\": {19},\n  \"apk_size_bytes\": {20},\n  \"elapsed_seconds\": {21:F6},\n  \"scenes\": [{22}]\n}}\n",
                EscapeJson(Application.unityVersion),
                EscapeJson(PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android)),
                EscapeJson(PhraseLayerLocalMarianAssets.ExpectedRevision),
                EscapeJson(visualEvidence.Name),
                visualEvidence.Length,
                EscapeJson(ocrManifest.Name),
                ocrManifest.Length,
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
