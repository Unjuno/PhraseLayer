using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Fail-closed guard for the official PhraseLayer build.
    ///
    /// PhraseLayer's reference distribution is local-only: no PhraseLayer backend, no remote inference,
    /// no telemetry, and no runtime Internet requirement. Engine interfaces remain replaceable, but the
    /// official Quest project must not accidentally acquire a network dependency.
    /// </summary>
    public sealed class PhraseLayerLocalOnlyBuildGuard : IPreprocessBuildWithReport
    {
        private static readonly string[] ForbiddenRuntimeNetworkMarkers =
        {
            "UnityEngine." + "Networking.",
            "System." + "Net.",
            "Http" + "Client",
            "Web" + "Client",
            "Web" + "Request.Create",
            "Tcp" + "Client",
            "Udp" + "Client",
        };

        private static readonly string[] ForbiddenDirectPackages =
        {
            "com.unity.services.analytics",
            "com.unity.services.authentication",
            "com.unity.services.cloudcode",
            "com.unity.services.remote-config",
        };

        public int callbackOrder => -10000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            VerifyCurrentProject(report.summary.platform);
        }

        [MenuItem("PhraseLayer/Verify Local-Only Contract")]
        public static void VerifyFromMenu()
        {
            VerifyCurrentProject(EditorUserBuildSettings.activeBuildTarget);
            Debug.Log("PhraseLayer local-only contract PASS: runtime source, package manifest, Android manifests, and forced Internet permission verified.");
        }

        [MenuItem("PhraseLayer/Apply Local-Only Android Defaults")]
        public static void ApplyLocalOnlyAndroidDefaults()
        {
            PlayerSettings.Android.forceInternetPermission = false;
            PlayerSettings.Android.forceSDCardPermission = false;
            AssetDatabase.SaveAssets();
            Debug.Log("PhraseLayer local-only Android defaults applied: forced Internet and external-storage permissions disabled.");
        }

        public static void VerifyCurrentProject()
        {
            VerifyCurrentProject(EditorUserBuildSettings.activeBuildTarget);
        }

        public static void VerifyCurrentProject(BuildTarget buildTarget)
        {
            var errors = new List<string>();
            var unityProjectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(unityProjectRoot))
                throw new BuildFailedException("Cannot resolve Unity project root from Application.dataPath.");

            var runtimeScripts = Path.Combine(unityProjectRoot, "Assets", "Scripts");
            ScanRuntimeSource(runtimeScripts, errors);

            var repoCore = Path.GetFullPath(Path.Combine(unityProjectRoot, "..", "..", "src", "PhraseLayer.Core"));
            ScanRuntimeSource(repoCore, errors);

            ScanAndroidManifests(Path.Combine(unityProjectRoot, "Assets"), errors);
            ValidatePackageManifest(Path.Combine(unityProjectRoot, "Packages", "manifest.json"), errors);

            if (buildTarget == BuildTarget.Android &&
                PlayerSettings.Android.forceInternetPermission)
            {
                errors.Add(
                    "PlayerSettings.Android.forceInternetPermission is enabled. " +
                    "The official PhraseLayer Quest build must not force android.permission.INTERNET.");
            }

            if (errors.Count > 0)
                throw new BuildFailedException("PhraseLayer local-only contract failed:\n- " + string.Join("\n- ", errors));
        }

        private static void ScanRuntimeSource(string root, ICollection<string> errors)
        {
            if (!Directory.Exists(root)) return;

            foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (IsEditorOnlyPath(path)) continue;
                var text = File.ReadAllText(path);
                foreach (var marker in ForbiddenRuntimeNetworkMarkers)
                {
                    if (text.IndexOf(marker, StringComparison.Ordinal) < 0) continue;
                    errors.Add("runtime networking marker '" + marker + "' found in " + RelativeProjectPath(path));
                }
            }
        }

        private static void ScanAndroidManifests(string assetsRoot, ICollection<string> errors)
        {
            if (!Directory.Exists(assetsRoot)) return;

            foreach (var path in Directory.EnumerateFiles(assetsRoot, "AndroidManifest.xml", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(path);
                if (text.IndexOf("android.permission.INTERNET", StringComparison.OrdinalIgnoreCase) >= 0)
                    errors.Add("Android manifest requests INTERNET: " + RelativeProjectPath(path));
                if (text.IndexOf("android.permission.ACCESS_NETWORK_STATE", StringComparison.OrdinalIgnoreCase) >= 0)
                    errors.Add("Android manifest requests ACCESS_NETWORK_STATE: " + RelativeProjectPath(path));
            }
        }

        private static void ValidatePackageManifest(string manifestPath, ICollection<string> errors)
        {
            if (!File.Exists(manifestPath))
            {
                errors.Add("Unity Packages/manifest.json is missing.");
                return;
            }

            var text = File.ReadAllText(manifestPath);
            foreach (var package in ForbiddenDirectPackages)
            {
                var quoted = "\"" + package + "\"";
                if (text.IndexOf(quoted, StringComparison.Ordinal) >= 0)
                    errors.Add("network/cloud service package is not allowed in the official build: " + package);
            }
        }

        private static bool IsEditorOnlyPath(string path)
        {
            var normalized = path.Replace('\\', '/');
            return normalized.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RelativeProjectPath(string path)
        {
            var root = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(root)) return path;

            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return fullPath;
            return fullPath.Substring(fullRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
