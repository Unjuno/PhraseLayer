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
    /// Real-Unity Android build gate for PhraseLayer. This is intentionally Editor-only: project settings are
    /// configured through the Unity API on the machine that actually owns the Android/IL2CPP modules rather than
    /// by committing a generated ProjectSettings.asset from an unverified environment.
    ///
    /// The build includes the deterministic demo scene and requires separately verified local Marian translation
    /// plus Moonshine ASR deployment assets before building. It does not silently download model files or permit
    /// the dictionary fallback in a build reported as the real Listen Mode product gate.
    /// </summary>
    public static class PhraseLayerAndroidBuild
    {
        private const string DefaultApplicationIdentifier = "com.unjuno.phraselayer";
        private const string BuildPathEnvironment = "PHRASELAYER_ANDROID_BUILD_PATH";
        private const string ApplicationIdentifierEnvironment = "PHRASELAYER_ANDROID_APPLICATION_ID";
        private const string DefaultRelativeBuildPath = "Builds/Android/PhraseLayer.apk";

        [MenuItem("PhraseLayer/Build Android ARM64 IL2CPP")]
        public static void Build()
        {
            EnsureAndroidTarget();
            ConfigureAndroidPlayer();
            PrepareDemoScene();

            var outputPath = ResolveOutputPath();
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new InvalidOperationException("Android build output path has no parent directory: " + outputPath);
            Directory.CreateDirectory(outputDirectory);

            var scenes = EditorBuildSettings.scenes
                .Where(scene => scene != null && scene.enabled && !string.IsNullOrWhiteSpace(scene.path))
                .Select(scene => scene.path)
                .ToArray();
            if (scenes.Length == 0)
                throw new InvalidOperationException("Android build requires at least one enabled build scene.");
            if (!scenes.Contains(PhraseLayerEditorSetup.DemoScenePath, StringComparer.Ordinal))
                throw new InvalidOperationException("Android build settings do not include the PhraseLayer demo scene.");

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
                throw new InvalidOperationException("Unity BuildPipeline returned no Android build report.");

            var summary = report.summary;
            WriteEvidence(outputPath, summary, scenes);
            if (summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "PhraseLayer Android build failed: result={0}; errors={1}; warnings={2}; time={3}.",
                        summary.result,
                        summary.totalErrors,
                        summary.totalWarnings,
                        summary.totalTime));
            }
            if (!File.Exists(outputPath))
                throw new FileNotFoundException("Unity reported success but the Android package is missing.", outputPath);

            Debug.Log(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "PhraseLayer Android ARM64 IL2CPP build PASS: {0}; bytes={1}; time={2}; warnings={3}; runtime=Marian+Moonshine.",
                    outputPath,
                    new FileInfo(outputPath).Length,
                    summary.totalTime,
                    summary.totalWarnings));
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
                    "This Unity installation does not have Android build support. Install the Android Build Support, SDK/NDK, and OpenJDK modules for Unity 6000.0.66f2.");
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
                throw new InvalidOperationException("Android scripting backend did not remain IL2CPP after configuration.");
            if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                throw new InvalidOperationException("Android architecture did not remain ARM64-only after configuration.");
        }

        private static void PrepareDemoScene()
        {
            PhraseLayerEditorSetup.CreateDemoScene();
            AssetDatabase.Refresh();

            // Both offline model stacks are required for the product-level Listen Mode build. Each assignment
            // performs real-Unity import/runtime checks and fails loudly if verified local assets are absent.
            PhraseLayerLocalTranslationAssets.AssignLocalAssetsToDemo();
            PhraseLayerLocalAsrAssets.AssignLocalAssetsToSceneBootstrap();
            if (!EditorSceneManager.SaveOpenScenes())
                throw new IOException("Failed to save PhraseLayer scenes after assigning verified offline model assets.");
            AssetDatabase.SaveAssets();
        }

        private static string ResolveOutputPath()
        {
            var configured = Environment.GetEnvironmentVariable(BuildPathEnvironment);
            if (!string.IsNullOrWhiteSpace(configured))
                return Path.GetFullPath(configured);
            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), DefaultRelativeBuildPath));
        }

        private static void WriteEvidence(string outputPath, BuildSummary summary, string[] scenes)
        {
            var outputDirectory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
            var evidencePath = Path.Combine(outputDirectory, "PhraseLayer.android-build-evidence.json");
            var apkSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0L;
            var sceneJson = string.Join(",", scenes.Select(scene => "\"" + EscapeJson(scene) + "\""));
            var json = string.Format(
                CultureInfo.InvariantCulture,
                "{{\n  \"schema_version\": 1,\n  \"purpose\": \"phrase-layer-android-arm64-il2cpp-build\",\n  \"unity_version\": \"{0}\",\n  \"application_identifier\": \"{1}\",\n  \"target\": \"Android\",\n  \"architecture\": \"ARM64\",\n  \"scripting_backend\": \"IL2CPP\",\n  \"translation_runtime\": \"Marian\",\n  \"asr_runtime\": \"MoonshineV1\",\n  \"dictionary_fallback_allowed\": false,\n  \"result\": \"{2}\",\n  \"total_errors\": {3},\n  \"total_warnings\": {4},\n  \"total_size_bytes_reported\": {5},\n  \"apk_size_bytes\": {6},\n  \"elapsed_seconds\": {7:F6},\n  \"scenes\": [{8}]\n}}\n",
                EscapeJson(Application.unityVersion),
                EscapeJson(PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android)),
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
