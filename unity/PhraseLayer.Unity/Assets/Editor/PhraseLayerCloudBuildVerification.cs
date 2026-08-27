using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Unity Build Automation / local player-build verification for PhraseLayer.
    ///
    /// UBA Pre-Export is the preparation boundary: it runs after script compilation and before the
    /// player build, so it can establish deterministic build settings (including a shell scene) before
    /// UBA resolves/exports the player. IPreprocessBuildWithReport is the fail-closed verification gate
    /// that runs again immediately before the player build.
    /// </summary>
    public sealed class PhraseLayerCloudBuildVerification : IPreprocessBuildWithReport
    {
        public const string PreExportMethodName =
            "PhraseLayer.Unity.Editor.PhraseLayerCloudBuildVerification.PreExport";

        // Local-only runs at -10000 and the Quest/MR contract at -9500. Run the wider Unity gate last.
        public int callbackOrder => -9000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));

            EnsureEnabledBuildSceneOrFail();
            PhraseLayerLocalOnlyBuildGuard.VerifyCurrentProject(report.summary.platform);
            if (report.summary.platform == BuildTarget.Android)
                PhraseLayerQuestMrBuildGuard.VerifyQuestMrContract();
            PhraseLayerEditorVerification.VerifyCorePipeline();

            Debug.Log("PhraseLayer Unity player-build gate PASS: " + report.summary.platform);
        }

        /// <summary>
        /// Configure this exact method in Unity Build Automation -> Advanced Settings -> Pre-Export Method.
        /// It intentionally has no CloudBuild-only parameter so the same source compiles and can be invoked
        /// locally for diagnosis.
        /// </summary>
        public static void PreExport()
        {
            Debug.Log("PhraseLayer UBA PreExport START: " + EditorUserBuildSettings.activeBuildTarget);

            PhraseLayerLocalOnlyBuildGuard.ApplyLocalOnlyAndroidDefaults();
            EnsureBuildScene();
            PhraseLayerLocalOnlyBuildGuard.VerifyCurrentProject(EditorUserBuildSettings.activeBuildTarget);
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android)
                PhraseLayerQuestMrBuildGuard.VerifyQuestMrContract();
            PhraseLayerEditorVerification.VerifyCorePipeline();

            Debug.Log("PhraseLayer UBA PreExport PASS: " + EditorUserBuildSettings.activeBuildTarget);
        }

        private static void EnsureBuildScene()
        {
            if (HasEnabledScene(EditorBuildSettings.scenes))
            {
                Debug.Log("PhraseLayer UBA preparation found an enabled build scene.");
                return;
            }

            // The repository currently carries a Unity shell rather than a committed production Quest scene.
            // PreExport is early enough to create the deterministic shell scene before the player build.
            PhraseLayerEditorSetup.CreateDemoScene();
            EnsureEnabledBuildSceneOrFail();
        }

        private static void EnsureEnabledBuildSceneOrFail()
        {
            if (HasEnabledScene(EditorBuildSettings.scenes)) return;

            throw new BuildFailedException(
                "PhraseLayer has no enabled build scene. In Unity Build Automation set Advanced Settings -> " +
                "Pre-Export Method to '" + PreExportMethodName + "' so the CI shell scene is created before export.");
        }

        private static bool HasEnabledScene(EditorBuildSettingsScene[] scenes)
        {
            if (scenes == null) return false;
            for (var i = 0; i < scenes.Length; i++)
            {
                var scene = scenes[i];
                if (scene != null && scene.enabled && !string.IsNullOrWhiteSpace(scene.path))
                    return true;
            }
            return false;
        }
    }
}
