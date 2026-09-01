using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Real-Unity capability preflight shared by host-only OCR/Read Mode and Marian gates. Runner labels are not
    /// treated as evidence: Unity itself must report the exact editor revision, Android build support and the reviewed
    /// Inference Engine compile branch before expensive local-model work begins. No local asset paths are serialized.
    /// </summary>
    public static class PhraseLayerUnityHostPreflight
    {
        public const string ExpectedUnityVersion = "6000.0.66f2";
        private const string EvidencePathEnvironment = "PHRASELAYER_UNITY_HOST_PREFLIGHT_EVIDENCE_PATH";
        private const string DefaultRelativeEvidencePath = "Temp/PhraseLayer.unity-host-preflight.json";

        [MenuItem("PhraseLayer/Validation/Run Unity Host Preflight")]
        public static void Run()
        {
            var actualVersion = Application.unityVersion ?? string.Empty;
            if (!string.Equals(actualVersion, ExpectedUnityVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "PhraseLayer host gates require Unity " + ExpectedUnityVersion +
                    " exactly; running editor is " + actualVersion + ".");
            }

            var androidSupported = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android);
            if (!androidSupported)
            {
                throw new InvalidOperationException(
                    "Unity Android Build Support is unavailable. Install Android Build Support plus the pinned editor SDK/NDK/OpenJDK modules before running PhraseLayer host gates.");
            }

            var inferenceCompileGateActive = IsInferenceCompileGateActive();
            if (!inferenceCompileGateActive)
            {
                throw new InvalidOperationException(
                    "PHRASELAYER_UNITY_AI_INFERENCE_2_2 is not active. Resolve the reviewed com.unity.ai.inference 2.2.x package before running PhraseLayer host gates.");
            }

            var projectRoot = ProjectRoot();
            var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
            var versionPath = Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt");
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length <= 0)
                throw new FileNotFoundException("Unity package manifest is missing or empty.", manifestPath);
            if (!File.Exists(versionPath) || new FileInfo(versionPath).Length <= 0)
                throw new FileNotFoundException("Unity ProjectVersion.txt is missing or empty.", versionPath);

            var evidencePath = ResolveEvidencePath(projectRoot);
            var parent = Path.GetDirectoryName(evidencePath);
            if (string.IsNullOrWhiteSpace(parent))
                throw new InvalidOperationException("Unity host preflight evidence path has no parent directory.");
            Directory.CreateDirectory(parent);

            var json = string.Format(
                CultureInfo.InvariantCulture,
                "{{\n  \"schema_version\": 1,\n  \"purpose\": \"phrase-layer-real-unity-host-preflight\",\n  \"real_unity_execution\": true,\n  \"expected_unity_version\": \"{0}\",\n  \"actual_unity_version\": \"{1}\",\n  \"exact_unity_version_match\": true,\n  \"android_build_support_available\": true,\n  \"inference_engine_compile_gate_active\": true,\n  \"package_manifest_present\": true,\n  \"project_version_file_present\": true,\n  \"project_paths_anchored_to_application_data_path\": true,\n  \"local_asset_paths_serialized\": false,\n  \"adb_required\": false,\n  \"quest_device_execution_performed\": false\n}}\n",
                EscapeJson(ExpectedUnityVersion),
                EscapeJson(actualVersion));
            File.WriteAllText(evidencePath, json);
            if (!File.Exists(evidencePath) || new FileInfo(evidencePath).Length <= 0)
                throw new IOException("Unity host preflight evidence was not written: " + evidencePath);

            Debug.Log("PhraseLayer Unity host preflight PASS: " + evidencePath);
        }

        public static void RunBatch()
        {
            try
            {
                Run();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static bool IsInferenceCompileGateActive()
        {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
            return true;
#else
            return false;
#endif
        }

        private static string ProjectRoot()
        {
            var assetsPath = Path.GetFullPath(Application.dataPath);
            var root = Path.GetDirectoryName(assetsPath);
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("Unable to derive Unity project root from Application.dataPath.");
            return Path.GetFullPath(root);
        }

        private static string ResolveEvidencePath(string projectRoot)
        {
            var configured = Environment.GetEnvironmentVariable(EvidencePathEnvironment);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                configured = configured.Trim();
                return Path.GetFullPath(Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(projectRoot, configured));
            }
            return Path.GetFullPath(Path.Combine(projectRoot, DefaultRelativeEvidencePath));
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
