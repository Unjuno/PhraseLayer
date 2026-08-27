using System;
using System.Reflection;
using UnityEngine;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Makes the committed Read MVP scene runnable without serializing local model binaries into source control.
    /// The scene pins Meta's PassthroughCameraAccess component; this installer creates the PhraseLayer runtime graph
    /// after scene load. Verified git-ignored local OCR/translation Resources configs are preferred when present;
    /// otherwise deterministic synthetic OCR and the explicit tiny translation dictionary remain safe fallbacks.
    /// </summary>
    public static class PhraseLayerReadMvpRuntimeInstaller
    {
        public const string MainCameraObjectName = "Main Camera";
        public const string PassthroughCameraObjectName = "PassthroughCameraAccess";
        public const string PassthroughCameraAccessTypeName = "Meta.XR.PassthroughCameraAccess";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallAfterSceneLoad()
        {
            InstallHeadTracking();

            if (HasPhraseLayerRuntime())
                return;

            var cameraAccess = FindCommittedPassthroughCameraAccess();
            if (cameraAccess == null)
                return;

            var localOcrConfig = Resources.Load<UnityLocalOcrRuntimeConfig>(UnityLocalOcrRuntimeConfig.ResourcesName);
            var useLocalOcr = localOcrConfig != null && localOcrConfig.IsConfigured;
            var localTranslationConfig = Resources.Load<UnityLocalTranslationRuntimeConfig>(UnityLocalTranslationRuntimeConfig.ResourcesName);
            var useLocalTranslation = localTranslationConfig != null && localTranslationConfig.IsConfigured;

            var root = new GameObject("PhraseLayer Read MVP Runtime");
            SetGameObjectActive(root, false);

            var cameraBridge = root.AddComponent<MetaPassthroughCameraBridge>();
            var presenter = root.AddComponent<OcrViewportDebugBehaviour>();
            var runtimeDriver = root.AddComponent<OcrDebugRuntimeBehaviour>();
            var learnerProfile = root.AddComponent<UnityLearnerProfileBehaviour>();
            var readAssistance = root.AddComponent<QuestReadAssistanceDebugBehaviour>();
            var worldOverlay = root.AddComponent<QuestReadWorldOverlayBehaviour>();

            UnityPaddleOcrBootstrapBehaviour ocrBootstrap = null;
            if (useLocalOcr)
                ocrBootstrap = root.AddComponent<UnityPaddleOcrBootstrapBehaviour>();

            UnityLocalTranslationAssetGateBehaviour translationAssetGate = null;
            UnityLocalTranslationBootstrapBehaviour translationBootstrap = null;
            if (useLocalTranslation)
            {
                translationAssetGate = root.AddComponent<UnityLocalTranslationAssetGateBehaviour>();
                translationBootstrap = root.AddComponent<UnityLocalTranslationBootstrapBehaviour>();
            }

            presenter.LoadSyntheticFixtureOnStart = !useLocalOcr;
            runtimeDriver.AutoRun = useLocalOcr;
            AssignReference(runtimeDriver, "cameraBridge", cameraBridge);
            AssignReference(runtimeDriver, "presenter", presenter);
            AssignReference(readAssistance, "ocrPresenter", presenter);
            AssignReference(readAssistance, "learnerProfile", learnerProfile);
            AssignReference(worldOverlay, "readAssistance", readAssistance);
            AssignReference(worldOverlay, "cameraBridge", cameraBridge);

            if (ocrBootstrap != null)
                localOcrConfig.ConfigureBootstrap(ocrBootstrap, runtimeDriver);
            else if (localOcrConfig != null)
                Debug.LogWarning("PhraseLayer local OCR runtime config exists but is incomplete; using synthetic OCR fallback. " + localOcrConfig.Status);

            if (translationBootstrap != null)
            {
                localTranslationConfig.ConfigureRuntime(
                    translationAssetGate,
                    translationBootstrap,
                    readAssistance);
            }
            else if (localTranslationConfig != null)
            {
                Debug.LogWarning(
                    "PhraseLayer local translation runtime config exists but is incomplete; using debug dictionary fallback. " +
                    localTranslationConfig.Status);
            }

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
                "HeadPose=UnityXR; OCR=" + (useLocalOcr ? "local-ppocr-camera" : "synthetic-fixture") +
                "; Translation=" + (useLocalTranslation ? "local-opus-mt" : "debug-dictionary") +
                "; WorldOverlay=native-environment+physics+viewport-fallback." +
                (useLocalOcr
                    ? " Verified local PP-OCR Resources config loaded; camera OCR auto-run enabled."
                    : " Stage/prepare verified local PP-OCR assets to enable real camera OCR.") +
                (useLocalTranslation
                    ? " Verified local OPUS-MT Resources config loaded; offline translation bootstrap enabled."
                    : " Stage/prepare a parity-verified local OPUS-MT bundle to replace the debug dictionary."));
        }

        private static void InstallHeadTracking()
        {
            var cameras = Resources.FindObjectsOfTypeAll<Camera>();
            for (var index = 0; index < cameras.Length; index++)
            {
                var camera = cameras[index];
                if (camera == null || camera.gameObject == null || !camera.gameObject.scene.IsValid())
                    continue;
                if (!string.Equals(camera.gameObject.name, MainCameraObjectName, StringComparison.Ordinal))
                    continue;

                camera.transform.localPosition = Vector3.zero;
                camera.transform.localRotation = Quaternion.identity;

                var drivers = Resources.FindObjectsOfTypeAll<UnityXrHeadPoseBehaviour>();
                for (var driverIndex = 0; driverIndex < drivers.Length; driverIndex++)
                {
                    var driver = drivers[driverIndex];
                    if (driver != null && ReferenceEquals(driver.gameObject, camera.gameObject))
                        return;
                }

                camera.gameObject.AddComponent<UnityXrHeadPoseBehaviour>();
                return;
            }

            Debug.LogWarning("PhraseLayer Read MVP could not find the scene Main Camera; XR head-pose tracking was not installed.");
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
