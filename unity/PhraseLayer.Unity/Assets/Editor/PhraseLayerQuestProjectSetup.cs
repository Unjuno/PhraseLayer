using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace PhraseLayer.Unity.Editor
{
    /// <summary>
    /// Batch bridge to Meta XR Core SDK's public Project Setup Tool API.
    ///
    /// PhraseLayer intentionally does not hand-author Unity XR settings YAML. A clean self-hosted checkout first
    /// switches to Android, then lets the pinned Meta SDK apply its current Required Quest configuration tasks.
    /// The process remains alive while FixAllAsync runs on EditorApplication.update and exits only after completion.
    /// </summary>
    public static class PhraseLayerQuestProjectSetup
    {
        private const string ProjectSetupTypeName = "OVRProjectSetup";

        public static async void ApplyAndroidRequiredFixesBatch()
        {
            try
            {
                EnsureAndroidTarget();
                var setupType = FindLoadedType(ProjectSetupTypeName);
                if (setupType == null)
                {
                    throw new InvalidOperationException(
                        "Could not resolve OVRProjectSetup. The pinned Meta XR Core SDK dependency must be imported before the Quest fixture build.");
                }

                var fixAll = setupType.GetMethod("FixAllAsync");
                if (fixAll == null)
                    throw new MissingMethodException(ProjectSetupTypeName, "FixAllAsync(BuildTargetGroup)");

                var parameters = fixAll.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(BuildTargetGroup))
                {
                    throw new MissingMethodException(
                        ProjectSetupTypeName,
                        "FixAllAsync(BuildTargetGroup) reviewed public API surface");
                }

                var result = fixAll.Invoke(null, new object[] { BuildTargetGroup.Android });
                var task = result as Task;
                if (task == null)
                {
                    throw new InvalidOperationException(
                        "OVRProjectSetup.FixAllAsync did not return a Task in the pinned Meta XR SDK.");
                }

                await task;
                AssetDatabase.SaveAssets();
                Debug.Log(
                    "PhraseLayer Quest project setup PASS: Meta Project Setup Tool completed Android Required fixes before the Read Mode fixture build.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(Unwrap(exception));
                EditorApplication.Exit(1);
            }
        }

        private static void EnsureAndroidTarget()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
            {
                throw new InvalidOperationException(
                    "This Unity installation does not have Android build support required for Quest project setup.");
            }
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
                !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
            {
                throw new InvalidOperationException("Unity failed to switch to Android before Meta Project Setup Tool execution.");
            }
        }

        private static Type FindLoadedType(string fullName)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var index = 0; index < assemblies.Length; index++)
            {
                var type = assemblies[index].GetType(fullName, throwOnError: false);
                if (type != null) return type;
            }
            return null;
        }

        private static Exception Unwrap(Exception exception)
        {
            var invocation = exception as System.Reflection.TargetInvocationException;
            return invocation != null && invocation.InnerException != null
                ? invocation.InnerException
                : exception;
        }
    }
}
