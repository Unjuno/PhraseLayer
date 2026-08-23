using System;

namespace UnityEngine
{
    public enum HideFlags { None = 0, HideAndDontSave = 61 }
    public enum FontStyle { Normal = 0, Bold = 1 }
    public enum FilterMode { Point = 0, Bilinear = 1, Trilinear = 2 }
    public enum TextureWrapMode { Repeat = 0, Clamp = 1 }
    public enum TextureFormat { RGBA32 = 4 }
    public enum RenderTextureFormat { ARGB32 = 0 }
    public enum RenderTextureReadWrite { Default = 0 }

    public class Object
    {
        public string name { get; set; }
        public HideFlags hideFlags { get; set; }
        public static void Destroy(Object obj) { }
        public static void DestroyImmediate(Object obj) { }
    }

    public class Component : Object
    {
        private readonly GameObject _gameObject = new GameObject("stub-component-owner");
        public GameObject gameObject => _gameObject;
    }

    public class Behaviour : Component { public bool enabled { get; set; } }
    public class MonoBehaviour : Behaviour { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class TextAreaAttribute : Attribute
    {
        public TextAreaAttribute(int minLines, int maxLines) { }
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

    public struct Vector4
    {
        public Vector4(float x, float y, float z, float w)
        { this.x = x; this.y = y; this.z = z; this.w = w; }
        public float x;
        public float y;
        public float z;
        public float w;
    }

    public struct Ray
    {
        public Ray(Vector3 origin, Vector3 direction) { this.origin = origin; this.direction = direction; }
        public Vector3 origin;
        public Vector3 direction;
    }

    public struct Rect
    {
        public Rect(float x, float y, float width, float height)
        { this.x = x; this.y = y; this.width = width; this.height = height; }
        public float x;
        public float y;
        public float width;
        public float height;
    }

    public struct Color32
    {
        public Color32(byte r, byte g, byte b, byte a)
        { this.r = r; this.g = g; this.b = b; this.a = a; }
        public byte r;
        public byte g;
        public byte b;
        public byte a;
    }

    public class Texture : Object
    {
        public int width { get; protected set; }
        public int height { get; protected set; }
        public FilterMode filterMode { get; set; }
        public TextureWrapMode wrapMode { get; set; }
    }

    public sealed class Texture2D : Texture
    {
        public Texture2D(int width, int height, TextureFormat format, bool mipChain, bool linear)
        { this.width = width; this.height = height; }
        public void ReadPixels(Rect source, int destX, int destY, bool recalculateMipMaps) { }
        public void Apply(bool updateMipmaps, bool makeNoLongerReadable) { }
        public Color32[] GetPixels32() => new Color32[Math.Max(1, width * height)];
        public void SetPixels32(Color32[] colors) { }
    }

    public sealed class RenderTexture : Texture
    {
        public RenderTexture(int width, int height, int depth, RenderTextureFormat format, RenderTextureReadWrite readWrite)
        { this.width = width; this.height = height; }

        public static RenderTexture active { get; set; }
        public bool useMipMap { get; set; }
        public bool autoGenerateMips { get; set; }
        public static RenderTexture GetTemporary(int width, int height, int depth, RenderTextureFormat format, RenderTextureReadWrite readWrite)
            => new RenderTexture(width, height, depth, format, readWrite);
        public static void ReleaseTemporary(RenderTexture texture) { }
        public void Create() { }
        public void Release() { }
    }

    public sealed class Shader : Object { }

    public sealed class Material : Object
    {
        public Material(Shader shader) { }
        public void SetVector(string name, Vector4 value) { }
        public void SetFloat(string name, float value) { }
    }

    public static class Graphics
    {
        public static void Blit(Texture source, RenderTexture dest) { }
        public static void Blit(Texture source, RenderTexture dest, Material material, int pass) { }
    }

    public static class Resources
    {
        public static T Load<T>(string path) where T : Object => null;
        public static T[] FindObjectsOfTypeAll<T>() where T : Object => Array.Empty<T>();
    }

    public sealed class TextAsset : Object
    {
        public string text { get; set; } = string.Empty;
        public byte[] bytes => Array.Empty<byte>();
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
        public GameObject(string name) { this.name = name; scene = new SceneManagement.Scene(); }
        public SceneManagement.Scene scene { get; set; }
        public T AddComponent<T>() where T : new() => new T();
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void Log(object message, Object context) { }
        public static void LogWarning(object message) { }
        public static void LogWarning(object message, Object context) { }
        public static void LogException(Exception exception) { }
        public static void LogException(Exception exception, Object context) { }
    }

    public static class Application
    {
        public static string dataPath => "Assets";
        public static string persistentDataPath => ".phraselayer-test-data";
    }

    public static class JsonUtility
    {
        public static string ToJson(object obj, bool prettyPrint = false) => "{}";
        public static T FromJson<T>(string json) => default(T);
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene
    {
        public bool IsValid() => true;
    }

    public enum NewSceneMode { Single = 0 }
}

namespace UnityEditor
{
    using UnityEngine;

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
        { this.path = path; this.enabled = enabled; }
        public string path { get; }
        public bool enabled { get; }
    }

    public static class EditorBuildSettings
    {
        public static EditorBuildSettingsScene[] scenes { get; set; } = Array.Empty<EditorBuildSettingsScene>();
    }

    public static class AssetDatabase
    {
        public static void Refresh() { }
        public static void SaveAssets() { }
        public static T LoadAssetAtPath<T>(string path) where T : Object => null;
        public static Object LoadMainAssetAtPath(string path) => null;
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

    public static class Undo
    {
        public static void RecordObject(Object obj, string name) { }
    }

    public sealed class SerializedProperty
    {
        public Object objectReferenceValue { get; set; }
        public bool boolValue { get; set; }
    }

    public sealed class SerializedObject
    {
        public SerializedObject(Object target) { }
        public SerializedProperty FindProperty(string propertyPath) => new SerializedProperty();
        public bool ApplyModifiedProperties() => false;
    }

    public static class EditorUtility
    {
        public static void SetDirty(Object target) { }
        public static bool IsPersistent(Object target) => false;
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

    public enum NewSceneSetup { DefaultGameObjects = 0 }

    public static class EditorSceneManager
    {
        public static Scene NewScene(NewSceneSetup setup, NewSceneMode mode) => new Scene();
        public static bool SaveScene(Scene scene, string path) => true;
    }
}
