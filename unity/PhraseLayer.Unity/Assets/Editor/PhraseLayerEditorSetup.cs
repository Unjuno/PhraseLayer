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
        private const string MetaEnvironmentRaycastManagerTypeName = "Meta.XR.EnvironmentRaycastManager";

        [MenuItem("PhraseLayer/Create or Reset Demo Scene")]
        public static void CreateDemoScene()
        {
            CreateDemoScene(null, null, false, null);
        }

        public static void CreateDemoScene(Font reviewedJapaneseFont, Material reviewedSourceMaskMaterial)
        {
            CreateDemoScene(reviewedJapaneseFont, reviewedSourceMaskMaterial, false, null);
        }

        public static void CreateDemoScene(
            Font reviewedJapaneseFont,
            Material reviewedSourceMaskMaterial,
            bool autoRunQuestReadModeSmoke)
        {
            CreateDemoScene(reviewedJapaneseFont, reviewedSourceMaskMaterial, autoRunQuestReadModeSmoke, null);
        }

        /// <summary>
        /// Deterministically creates the demo scene and optionally injects locally reviewed visual assets.
        /// Quest physical text placement uses MRUK EnvironmentRaycastManager against live depth, so no prior Scene
        /// scan or generated collider mesh is required. The explicit smoke autorun flag is reserved for the
        /// instrumented Quest fixture build; ordinary editor scene creation never starts a hardware gate.
        /// The optional root configurator is a narrow extension point for product-specific adapters such as the
        /// reviewed offline Marian bootstrap; the base Read Mode scene stays independent of concrete translation runtimes.
        /// </summary>
        public static void CreateDemoScene(
            Font reviewedJapaneseFont,
            Material reviewedSourceMaskMaterial,
            bool autoRunQuestReadModeSmoke,
            Action<GameObject, PhraseLayerDemoBehaviour> configureRoot)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var root = new GameObject("PhraseLayer Demo");

            var demo = root.AddComponent<PhraseLayerDemoBehaviour>();
            var presenter = root.AddComponent<OcrViewportDebugBehaviour>();
            var cameraBridge = root.AddComponent<MetaPassthroughCameraBridge>();
            var runtimeDriver = root.AddComponent<OcrDebugRuntimeBehaviour>();
            var ocrBootstrap = root.AddComponent<UnityPaddleOcrBootstrapBehaviour>();
            var questOcrSmoke = root.AddComponent<QuestOcrSmokeTestBehaviour>();
            var environmentSurfaceRaycaster = root.AddComponent<UnityEnvironmentSurfaceRaycaster>();
            var spatialProjection = root.AddComponent<UnitySpatialProjectionBehaviour>();
            var worldTextTracking = root.AddComponent<UnityWorldTextTrackingBehaviour>();
            var worldTextSourceMask = root.AddComponent<UnityWorldTextSourceMaskBehaviour>();
            var worldTextRenderer = root.AddComponent<UnityWorldTextRendererBehaviour>();
            var liveReadMode = root.AddComponent<UnityLiveReadModeBehaviour>();
            var questReadModeSmoke = root.AddComponent<QuestReadModeSmokeTestBehaviour>();
            var metaCamera = AddRequiredMetaComponent(root, MetaPassthroughCameraAccessTypeName);
            var environmentRaycastManager = AddRequiredMetaComponent(root, MetaEnvironmentRaycastManagerTypeName);

            cameraBridge.SetPassthroughCameraAccess(metaCamera);
            runtimeDriver.SetSceneReferences(cameraBridge, presenter);
            ocrBootstrap.SetRuntimeDriver(runtimeDriver);
            questOcrSmoke.SetSceneReferences(runtimeDriver, presenter, ocrBootstrap);
            environmentSurfaceRaycaster.SetEnvironmentRaycastManager(environmentRaycastManager);
            spatialProjection.SetSceneReferences(cameraBridge, environmentSurfaceRaycaster);
            worldTextTracking.SetProjection(spatialProjection);
            worldTextTracking.SetSourceMask(worldTextSourceMask);
            worldTextTracking.SetRenderer(worldTextRenderer);
            liveReadMode.SetSceneReferences(presenter, worldTextTracking);
            questReadModeSmoke.SetSceneReferences(questOcrSmoke, liveReadMode, worldTextTracking);
            questReadModeSmoke.AutoRunOnStart = autoRunQuestReadModeSmoke;
            demo.SetLiveReadMode(liveReadMode);

            configureRoot?.Invoke(root, demo);

            if (reviewedJapaneseFont != null)
                worldTextRenderer.SetFont(reviewedJapaneseFont);
            if (reviewedSourceMaskMaterial != null)
                worldTextSourceMask.SetMaskMaterial(reviewedSourceMaskMaterial);

            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Scenes"));
            if (!EditorSceneManager.SaveScene(scene, DemoScenePath))
                throw new IOException("Failed to save PhraseLayer demo scene.");

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(DemoScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log(
                "PhraseLayer demo scene created with Meta camera → real-device OCR smoke → latest-only adaptive Read Mode → semantic geometry → MRUK live-depth environment raycast → four-corner surface fit → temporal tracking → confidence-gated source mask → font-injected world text renderer → end-to-end Quest Read Mode smoke wiring. The default demo language pipeline remains dictionary-based unless a product-specific root configurator injects another translation runtime. Assign both a reviewed opaque source-mask Material and a reviewed Japanese-capable Font before claiming complete in-place replacement: " +
                DemoScenePath);
        }

        private static Component AddRequiredMetaComponent(GameObject root, string fullTypeName)
        {
            var componentType = FindLoadedType(fullTypeName);
            if (componentType == null)
            {
                throw new InvalidOperationException(
                    "Could not resolve " + fullTypeName +
                    ". Resolve the pinned Meta XR/MRUK packages before creating the Quest Read Mode demo scene.");
            }
            if (!typeof(Component).IsAssignableFrom(componentType))
                throw new InvalidOperationException(fullTypeName + " is not a UnityEngine.Component in the installed Meta package.");

            return root.AddComponent(componentType);
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