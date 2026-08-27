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
    /// Android build-time guard for the small set of Meta/OpenXR assets that the Quest Read vertical slice requires.
    /// Keep this text-based on purpose: it validates the serialized project that Unity Build Automation actually
    /// checked out, without depending on Meta editor-only project-setup APIs or creating telemetry-bearing objects.
    /// </summary>
    public sealed class PhraseLayerQuestMrBuildGuard : IPreprocessBuildWithReport
    {
        private const string RequiredMrukPackage = "\"com.meta.xr.mrutilitykit\": \"85.0.0\"";
        private const string MetaAndroidFeatureName = "m_Name: MetaXRFeature Android";
        private const string EnvironmentRaycastExtension = "XR_META_environment_raycast";

        public int callbackOrder => -9000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (report.summary.platform != BuildTarget.Android) return;
            VerifyQuestMrContract();
        }

        [MenuItem("PhraseLayer/Verify Quest MR Build Contract")]
        public static void VerifyFromMenu()
        {
            VerifyQuestMrContract();
            Debug.Log("PhraseLayer Quest MR build contract PASS: MRUK 85, Android Meta XR Feature, environment raycast, linker preservation, and scene permission verified.");
        }

        public static void VerifyQuestMrContract()
        {
            var errors = new List<string>();
            var unityProjectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(unityProjectRoot))
                throw new BuildFailedException("Cannot resolve Unity project root from Application.dataPath.");

            ValidatePackageManifest(Path.Combine(unityProjectRoot, "Packages", "manifest.json"), errors);
            ValidateOpenXrSettings(Path.Combine(unityProjectRoot, "Assets", "XR", "Settings", "OpenXR Package Settings.asset"), errors);
            ValidateLinkXml(Path.Combine(unityProjectRoot, "Assets", "link.xml"), errors);
            ValidateAndroidManifest(Path.Combine(unityProjectRoot, "Assets", "Plugins", "Android", "AndroidManifest.xml"), errors);

            if (errors.Count > 0)
            {
                throw new BuildFailedException(
                    "PhraseLayer Quest MR build contract failed:\n- " + string.Join("\n- ", errors));
            }
        }

        private static void ValidatePackageManifest(string path, ICollection<string> errors)
        {
            var text = ReadRequiredFile(path, "Packages/manifest.json", errors);
            if (text == null) return;
            if (text.IndexOf(RequiredMrukPackage, StringComparison.Ordinal) < 0)
                errors.Add("MRUK package must remain pinned to 85.0.0 for the reviewed native environment-raycast ABI.");
        }

        private static void ValidateOpenXrSettings(string path, ICollection<string> errors)
        {
            var text = ReadRequiredFile(path, "Assets/XR/Settings/OpenXR Package Settings.asset", errors);
            if (text == null) return;

            var sections = text.Split(new[] { "--- !u!114" }, StringSplitOptions.None);
            string androidMetaSection = null;
            var matchCount = 0;
            foreach (var section in sections)
            {
                if (section.IndexOf(MetaAndroidFeatureName, StringComparison.Ordinal) < 0) continue;
                androidMetaSection = section;
                matchCount++;
            }

            if (matchCount != 1 || androidMetaSection == null)
            {
                errors.Add("OpenXR settings must contain exactly one Android Meta XR Feature section.");
                return;
            }

            if (androidMetaSection.IndexOf("m_enabled: 1", StringComparison.Ordinal) < 0)
                errors.Add("Android Meta XR Feature is disabled.");
            if (androidMetaSection.IndexOf(EnvironmentRaycastExtension, StringComparison.Ordinal) < 0)
                errors.Add("Android Meta XR Feature does not expose XR_META_environment_raycast.");
        }

        private static void ValidateLinkXml(string path, ICollection<string> errors)
        {
            var text = ReadRequiredFile(path, "Assets/link.xml", errors);
            if (text == null) return;
            if (text.IndexOf("<assembly fullname=\"meta.xr.mrutilitykit\">", StringComparison.Ordinal) < 0 ||
                text.IndexOf("<type fullname=\"Meta.XR.MRUtilityKit.MRUKNativeFuncs*\" preserve=\"all\" />", StringComparison.Ordinal) < 0)
            {
                errors.Add("link.xml must preserve MRUKNativeFuncs reflection targets for IL2CPP.");
            }
        }

        private static void ValidateAndroidManifest(string path, ICollection<string> errors)
        {
            var text = ReadRequiredFile(path, "Assets/Plugins/Android/AndroidManifest.xml", errors);
            if (text == null) return;
            if (text.IndexOf("com.oculus.permission.USE_SCENE", StringComparison.Ordinal) < 0)
                errors.Add("Android manifest is missing com.oculus.permission.USE_SCENE.");
            if (text.IndexOf("horizonos.permission.HEADSET_CAMERA", StringComparison.Ordinal) < 0)
                errors.Add("Android manifest is missing horizonos.permission.HEADSET_CAMERA.");
            if (text.IndexOf("com.oculus.feature.PASSTHROUGH", StringComparison.Ordinal) < 0)
                errors.Add("Android manifest is missing required passthrough feature declaration.");
        }

        private static string ReadRequiredFile(string path, string displayPath, ICollection<string> errors)
        {
            if (!File.Exists(path))
            {
                errors.Add(displayPath + " is missing.");
                return null;
            }

            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception exception)
            {
                errors.Add(displayPath + " could not be read: " + exception.Message);
                return null;
            }
        }
    }
}
