using System;

namespace UnityEngine
{
    public class Object { }
    public class Component : Object { }
    public class Behaviour : Component { public bool enabled { get; set; } }
    public class MonoBehaviour : Behaviour { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class TextAreaAttribute : Attribute
    {
        public TextAreaAttribute(int minLines, int maxLines) { }
    }

    public enum FontStyle
    {
        Normal = 0,
        Bold = 1
    }

    public struct Vector2
    {
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public float x;
        public float y;
    }

    public struct Vector3
    {
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float x;
        public float y;
        public float z;
    }

    public struct Ray
    {
        public Ray(Vector3 origin, Vector3 direction) { this.origin = origin; this.direction = direction; }
        public Vector3 origin;
        public Vector3 direction;
    }

    public class Texture : Object
    {
        public int width { get; set; }
        public int height { get; set; }
    }

    public struct Rect
    {
        public Rect(float x, float y, float width, float height)
        {
            this.x = x;
            this.y = y;
            this.width = width;
            this.height = height;
        }

        public float x;
        public float y;
        public float width;
        public float height;
    }

    public sealed class RectOffset
    {
        public RectOffset(int left, int right, int top, int bottom) { }
    }

    public class GUIStyle
    {
        public GUIStyle() { }
        public GUIStyle(GUIStyle other) { }
        public RectOffset padding { get; set; }
        public int fontSize { get; set; }
        public FontStyle fontStyle { get; set; }
        public bool wordWrap { get; set; }
    }

    public sealed class GUISkin
    {
        public GUIStyle box { get; } = new GUIStyle();
        public GUIStyle label { get; } = new GUIStyle();
    }

    public static class GUI
    {
        public static GUISkin skin { get; } = new GUISkin();
        public static void Box(Rect position, string text) { }
    }

    public sealed class GUILayoutOption { }

    public static class GUILayout
    {
        public static void BeginArea(Rect screenRect, GUIStyle style) { }
        public static Vector2 BeginScrollView(Vector2 scrollPosition) => scrollPosition;
        public static void Label(string text, GUIStyle style) { }
        public static void Space(float pixels) { }
        public static void BeginHorizontal() { }
        public static bool Button(string text, params GUILayoutOption[] options) => false;
        public static GUILayoutOption Height(float height) => new GUILayoutOption();
        public static void EndHorizontal() { }
        public static void EndScrollView() { }
        public static void EndArea() { }
    }

    public static class Mathf
    {
        public static int Min(int a, int b) => Math.Min(a, b);
        public static int Max(int a, int b) => Math.Max(a, b);
    }

    public static class Screen
    {
        public static int width => 1920;
        public static int height => 1080;
    }

    public static class Time
    {
        public static double realtimeSinceStartupAsDouble => 0.0;
    }

    public sealed class GameObject : Object
    {
        public GameObject(string name) { }
        public T AddComponent<T>() where T : new() => new T();
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogException(Exception exception) { }
        public static void LogException(Exception exception, Object context) { }
    }

    public static class Application
    {
        public static string dataPath => "Assets";
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene { }

    public enum NewSceneMode
    {
        Single = 0
    }
}

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItem : Attribute
    {
        public MenuItem(string itemName) { }
    }

    public enum BuildTarget
    {
        NoTarget = -2,
        Android = 13,
        StandaloneWindows64 = 19
    }

    public sealed class EditorBuildSettingsScene
    {
        public EditorBuildSettingsScene(string path, bool enabled)
        {
            this.path = path;
            this.enabled = enabled;
        }

        public string path { get; }
        public bool enabled { get; }
    }

    public static class EditorBuildSettings
    {
        public static EditorBuildSettingsScene[] scenes { get; set; } = Array.Empty<EditorBuildSettingsScene>();
    }

    public static class AssetDatabase
    {
        public static void SaveAssets() { }
    }

    public static class EditorApplication
    {
        public static void Exit(int returnValue) { }
    }

    public static class EditorUserBuildSettings
    {
        public static BuildTarget activeBuildTarget => BuildTarget.Android;
    }

    public static class PlayerSettings
    {
        public static class Android
        {
            public static bool forceInternetPermission { get; set; }
            public static bool forceSDCardPermission { get; set; }
        }
    }
}

namespace UnityEditor.Build
{
    using UnityEditor.Build.Reporting;

    public interface IPreprocessBuildWithReport
    {
        int callbackOrder { get; }
        void OnPreprocessBuild(BuildReport report);
    }

    public sealed class BuildFailedException : Exception
    {
        public BuildFailedException(string message) : base(message) { }
    }
}

namespace UnityEditor.Build.Reporting
{
    using UnityEditor;

    public sealed class BuildSummary
    {
        public BuildTarget platform { get; set; }
    }

    public sealed class BuildReport
    {
        public BuildSummary summary { get; } = new BuildSummary();
    }
}

namespace UnityEditor.SceneManagement
{
    using UnityEngine.SceneManagement;

    public enum NewSceneSetup
    {
        DefaultGameObjects = 0
    }

    public static class EditorSceneManager
    {
        public static Scene NewScene(NewSceneSetup setup, NewSceneMode mode) => new Scene();
        public static bool SaveScene(Scene scene, string path) => true;
    }
}
