using System;
using System.Reflection;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Makes the committed Read MVP scene runnable without serializing PhraseLayer MonoBehaviours or local model
    /// assets into source control. The scene pins Meta's PassthroughCameraAccess component; this installer creates
    /// the PhraseLayer runtime graph after scene load and uses the synthetic OCR fixture until a developer regenerates
    /// the same scene with locally staged PP-OCR assets through PhraseLayerReadMvpSceneSetup.
    /// </summary>
    public static class PhraseLayerReadMvpRuntimeInstaller
    {
        public const string PassthroughCameraObjectName = "PassthroughCameraAccess";
        public const string PassthroughCameraAccessTypeName = "Meta.XR.PassthroughCameraAccess";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallAfterSceneLoad()
        {
            if (HasPhraseLayerRuntime())
                return;

            var cameraAccess = FindCommittedPassthroughCameraAccess();
            if (cameraAccess == null)
                return;

            var root = new GameObject("PhraseLayer Read MVP Runtime");
            SetGameObjectActive(root, false);

            var cameraBridge = root.AddComponent<MetaPassthroughCameraBridge>();
            var presenter = root.AddComponent<OcrViewportDebugBehaviour>();
            var runtimeDriver = root.AddComponent<OcrDebugRuntimeBehaviour>();
            var learnerProfile = root.AddComponent<UnityLearnerProfileBehaviour>();
            var readAssistance = root.AddComponent<QuestReadAssistanceDebugBehaviour>();

            presenter.LoadSyntheticFixtureOnStart = true;
            runtimeDriver.AutoRun = false;
            AssignReference(runtimeDriver, "cameraBridge", cameraBridge);
            AssignReference(runtimeDriver, "presenter", presenter);
            AssignReference(readAssistance, "ocrPresenter", presenter);
            AssignReference(readAssistance, "learnerProfile", learnerProfile);

            try
            {
                cameraBridge.SetPassthroughCameraAccess(cameraAccess);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "Committed Meta PassthroughCameraAccess was found but its public API no longer matches the reviewed bridge contract: " +
                    exception.Message);
            }

            SetGameObjectActive(root, true);
            Debug.Log(
                "PhraseLayer committed Read MVP runtime installed. " +
                "OCR=synthetic-fixture; stage PP-OCR assets and run PhraseLayer/Read MVP/Create or Reset Local Read Scene for real camera OCR.");
        }

        private static bool HasPhraseLayerRuntime()
        {
            var bridges = Resources.FindObjectsOfTypeAll<MetaPassthroughCameraBridge>();
            return bridges != null && bridges.Length > 0;
        }

        private static Component FindCommittedPassthroughCameraAccess()
        {
            var components = Resources.FindObjectsOfTypeAll<Component>();
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                if (component == null || component.gameObject == null)
                    continue;
                if (!string.Equals(component.gameObject.name, PassthroughCameraObjectName, StringComparison.Ordinal))
                    continue;
                if (string.Equals(component.GetType().FullName, PassthroughCameraAccessTypeName, StringComparison.Ordinal))
                    return component;
            }

            return null;
        }

        private static void AssignReference(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new InvalidOperationException(target.GetType().Name + " private scene field missing: " + fieldName);
            field.SetValue(target, value);
        }

        private static void SetGameObjectActive(GameObject gameObject, bool active)
        {
            var method = typeof(GameObject).GetMethod(
                "SetActive",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(bool) },
                null);
            if (method == null)
                throw new MissingMethodException(typeof(GameObject).FullName, "SetActive(bool)");
            method.Invoke(gameObject, new object[] { active });
        }
    }
}
