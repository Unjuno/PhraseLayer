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

        [MenuItem("PhraseLayer/Create or Reset Demo Scene")]
        public static void CreateDemoScene()
        {
            PhraseLayerLocalOnlyBuildGuard.ApplyLocalOnlyAndroidDefaults();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var root = new GameObject("PhraseLayer Demo");
            root.AddComponent<PhraseLayerDemoBehaviour>();
            root.AddComponent<OcrViewportDebugBehaviour>();

            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Scenes"));
            if (!EditorSceneManager.SaveScene(scene, DemoScenePath))
                throw new IOException("Failed to save PhraseLayer demo scene.");

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(DemoScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("PhraseLayer demo scene created with local-only Android defaults: " + DemoScenePath);
        }

        public static void CreateDemoSceneBatch()
        {
            CreateDemoScene();
            EditorApplication.Exit(0);
        }
    }
}
