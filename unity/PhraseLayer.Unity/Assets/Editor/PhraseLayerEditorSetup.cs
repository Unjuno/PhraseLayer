using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PhraseLayer.Unity.Editor
{
    public static class PhraseLayerEditorSetup
    {
        public const string DemoScenePath = "Assets/Scenes/PhraseLayerDemo.unity";
        private const string MetaPassthroughCameraAccessTypeName = "Meta.XR.PassthroughCameraAccess";

        [MenuItem("PhraseLayer/Create or Reset Demo Scene")]
        public static void CreateDemoScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var root = new GameObject("PhraseLayer Demo");

            var demo = root.AddComponent<PhraseLayerDemoBehaviour>();
            var presenter = root.AddComponent<OcrViewportDebugBehaviour>();
            var cameraBridge = root.AddComponent<MetaPassthroughCameraBridge>();
            var runtimeDriver = root.AddComponent<OcrDebugRuntimeBehaviour>();
            var ocrBootstrap = root.AddComponent<UnityPaddleOcrBootstrapBehaviour>();
            var surfaceRaycaster = root.AddComponent<UnityPhysicsSurfaceRaycaster>();
            var spatialProjection = root.AddComponent<UnitySpatialProjectionBehaviour>();
            var worldTextTracking = root.AddComponent<UnityWorldTextTrackingBehaviour>();
            var worldTextSourceMask = root.AddComponent<UnityWorldTextSourceMaskBehaviour>();
            var worldTextRenderer = root.AddComponent<UnityWorldTextRendererBehaviour>();
            var liveReadMode = root.AddComponent<UnityLiveReadModeBehaviour>();
            var metaCamera = AddMetaPassthroughCameraAccess(root);

            // SetPassthroughCameraAccess validates the installed Meta API surface immediately. If the pinned
            // MRUK package drifts away from IsPlaying/GetTexture/ViewportPointToRay, scene creation fails loudly.
            cameraBridge.SetPassthroughCameraAccess(metaCamera);
            runtimeDriver.SetSceneReferences(cameraBridge, presenter);
            ocrBootstrap.SetRuntimeDriver(runtimeDriver);
            spatialProjection.SetSceneReferences(cameraBridge, surfaceRaycaster);
            worldTextTracking.SetProjection(spatialProjection);
            worldTextTracking.SetSourceMask(worldTextSourceMask);
            worldTextTracking.SetRenderer(worldTextRenderer);
            liveReadMode.SetSceneReferences(presenter, worldTextTracking);
            demo.SetLiveReadMode(liveReadMode);

            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Scenes"));
            if (!EditorSceneManager.SaveScene(scene, DemoScenePath))
                throw new IOException("Failed to save PhraseLayer demo scene.");

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(DemoScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log(
                "PhraseLayer demo scene created with Meta camera → one-pass OCR → latest-only adaptive Read Mode → semantic geometry → four-corner surface fit → temporal tracking → confidence-gated source mask → font-injected world text renderer wiring. The demo language pipeline remains dictionary-based. Assign both a reviewed opaque source-mask Material and a reviewed Japanese-capable Font before claiming complete in-place replacement: " +
                DemoScenePath);
        }

        private static Component AddMetaPassthroughCameraAccess(GameObject root)
        {
            var cameraType = FindLoadedType(MetaPassthroughCameraAccessTypeName);
            if (cameraType == null)
            {
                throw new InvalidOperationException(
                    "Could not resolve " + MetaPassthroughCameraAccessTypeName +
                    ". Resolve the pinned com.meta.xr.mrutilitykit package before creating the Quest OCR demo scene.");
            }
            if (!typeof(Component).IsAssignableFrom(cameraType))
            {
                throw new InvalidOperationException(
                    MetaPassthroughCameraAccessTypeName + " is not a UnityEngine.Component in the installed Meta package.");
            }

            return root.AddComponent(cameraType);
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

        public static void CreateDemoSceneBatch()
        {
            CreateDemoScene();
            EditorApplication.Exit(0);
        }
    }
}
