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
    /// Android ARM64 IL2CPP build used only for the Quest Read Mode hardware/visual vertical-slice gate.
    ///
    /// This intentionally keeps the demo dictionary translation path so camera/OCR, surface fitting, masking and
    /// Japanese world-text rendering can be validated independently of Marian. Evidence therefore records
    /// product_translation_gate=false and translation_runtime=DemoDictionaryFixture; this build must never be
    /// presented as the final offline NMT product build.
    /// </summary>
    public static class PhraseLayerReadModeFixtureAndroidBuild
    {
        private const string DefaultApplicationIdentifier = "com.unjuno.phraselayer.readmodefixture";
        private const string BuildPathEnvironment = "PHRASELAYER_READ_MODE_FIXTURE_APK_PATH";
        private const string ApplicationIdentifierEnvironment = "PHRASELAYER_READ_MODE_FIXTURE_APPLICATION_ID";
        private const string DefaultRelativeBuildPath = "Builds/Android/PhraseLayerReadModeFixture.apk";
        private const string VisualEvidenceRelativePath = "Assets/LocalReadModeAssets/read-mode-visual-assets.json";
        private const string OcrManifestRelativePath = "Assets/LocalOcrAssets/PaddleOCR/PhraseLayerOcrAssets.manifest.json";

        [MenuItem("PhraseLayer/Read Mode/Build Quest Fixture Android ARM64 IL2CPP")]
        public static void Build()
        {
            EnsureAndroidTarget();
            ConfigureAndroidPlayer();

            PhraseLayerLocalReadModeVisualAssets.StageAndCreateDemoScene(autoRunQuestReadModeSmoke: true);
            PhraseLayerLocalOcrAssets.AssignLocalAssetsToSceneBootstrap();
            if (!EditorSceneManager.SaveOpenScenes())
                throw new IOException("Failed to save PhraseLayer Read Mode fixture scene after OCR assignment.");
            AssetDatabase.SaveAssets();

            var visualEvidence = RequireProjectFile(VisualEvidenceRelativePath);
            var ocrManifest = RequireProjectFile(OcrManifestRelativePath);
            var outputPath = ResolveOutputPath();
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException("Read Mode fixture output path has no parent directory: " + outputPath);
            Directory.CreateDirectory(outputDirectory);

            var scenes = EditorBuildSettings.scenes
                .Where(scene => scene != null && scene.enabled && !string.IsNullOrWhiteSpace(scene.path))
                .Select(scene => scene.path)
                .ToArray();
            if (scenes.Length == 0)
                throw new InvalidOperationException("Read Mode fixture build requires at least one enabled build scene.");
            if (!scenes.Contains(PhraseLayerEditorSetup.DemoScenePath, StringComparer.Ordinal))
                throw new InvalidOperationException("Read Mode fixture build settings do not include the PhraseLayer demo scene.");

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
                throw new InvalidOperationException("Unity BuildPipeline returned no Read Mode fixture report.");

            var summary = report.summary;
            WriteEvidence(outputPath, summary, scenes, visualEvidence, ocrManifest);
            if (summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "PhraseLayer Read Mode fixture build failed: result={0}; errors={1}; warnings={2}; time={3}.",
                        summary.result,
                        summary.totalErrors,
                        summary.totalWarnings,
                        summary.totalTime));
            }
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
                throw new FileNotFoundException("Unity reported success but the Read Mode fixture APK is missing or empty.", outputPath);

            Debug.Log(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "PhraseLayer Read Mode Quest fixture Android build PASS: {0}; bytes={1}; time={2}; runtime=PaddleOCR+DemoDictionaryFixture; product_translation_gate=false.",
                    outputPath,
                    new FileInfo(outputPath).Length,
                    summary.totalTime));
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

            PlayerSettings.SetApplicationIdentifier(namedTarget, applicationIdentifier);
            PlayerSettings.SetScriptingBackend(namedTarget, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;

            if (PlayerSettings.GetScriptingBackend(namedTarget) != ScriptingImplementation.IL2CPP)
                throw new InvalidOperationException("Read Mode fixture scripting backend did not remain IL2CPP.");
            if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                throw new InvalidOperationException("Read Mode fixture architecture did not remain ARM64-only.");
        }

        private static string ResolveOutputPath()
        {
            var configured = Environment.GetEnvironmentVariable(BuildPathEnvironment);
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured);
            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), DefaultRelativeBuildPath));
        }

        private static FileInfo RequireProjectFile(string assetRelativePath)
        {
            var path = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), assetRelativePath));
            if (!File.Exists(path) || new FileInfo(path).Length <= 0)
                throw new FileNotFoundException("Required Read Mode fixture evidence is missing or empty.", path);
            return new FileInfo(path);
        }

        private static void WriteEvidence(
            string outputPath,
            BuildSummary summary,
            string[] scenes,
            FileInfo visualEvidence,
            FileInfo ocrManifest)
        {
            var outputDirectory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
            var evidencePath = Path.Combine(outputDirectory, "PhraseLayer.read-mode-fixture-build-evidence.json");
            var apkSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0L;
            var sceneJson = string.Join(",", scenes.Select(scene => "\"" + EscapeJson(scene) + "\""));
            var json = string.Format(
                CultureInfo.InvariantCulture,
                "{{\n  \"schema_version\": 1,\n  \"purpose\": \"phrase-layer-read-mode-quest-fixture-build\",\n  \"unity_version\": \"{0}\",\n  \"application_identifier\": \"{1}\",\n  \"target\": \"Android\",\n  \"architecture\": \"ARM64\",\n  \"scripting_backend\": \"IL2CPP\",\n  \"ocr_runtime\": \"PaddleOCR\",\n  \"translation_runtime\": \"DemoDictionaryFixture\",\n  \"product_translation_gate\": false,\n  \"quest_read_mode_smoke_autorun\": true,\n  \"source_mask_shader\": \"PhraseLayer/SourceMask\",\n  \"visual_asset_evidence_file\": \"{2}\",\n  \"visual_asset_evidence_size_bytes\": {3},\n  \"ocr_asset_manifest_file\": \"{4}\",\n  \"ocr_asset_manifest_size_bytes\": {5},\n  \"result\": \"{6}\",\n  \"total_errors\": {7},\n  \"total_warnings\": {8},\n  \"total_size_bytes_reported\": {9},\n  \"apk_size_bytes\": {10},\n  \"elapsed_seconds\": {11:F6},\n  \"scenes\": [{12}]\n}}\n",
                EscapeJson(Application.unityVersion),
                EscapeJson(PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android)),
                EscapeJson(visualEvidence.Name),
                visualEvidence.Length,
                EscapeJson(ocrManifest.Name),
                ocrManifest.Length,
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
    }
}
