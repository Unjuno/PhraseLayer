using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PhraseLayer.Core.Pipeline;
using PhraseLayer.Core.Translation;

namespace UnityEngine
{
    public class Object { }
    public class Component : Object { }
    public class Behaviour : Component { public bool enabled { get; set; } = true; }
    public class MonoBehaviour : Behaviour { }

    public sealed class GameObject : Object
    {
        public GameObject(string name) { }
        public T AddComponent<T>() where T : Component, new() => new T();
    }

    public sealed class TextAsset : Object
    {
        public string text { get; set; } = string.Empty;
        public byte[] bytes { get; set; } = Array.Empty<byte>();
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    public static class JsonUtility
    {
        public static T FromJson<T>(string json) where T : class => null;
    }

    public static class Resources
    {
        public static T Load<T>(string path) where T : Object => null;
    }

    public static class Time
    {
        public static double realtimeSinceStartupAsDouble => 0.0;
    }

    public static class Application
    {
        public static string dataPath => "Assets";
        public static string unityVersion => "6000.0.66f2";
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void Log(object message, Object context) { }
        public static void LogError(object message, Object context) { }
        public static void LogException(Exception exception) { }
        public static void LogException(Exception exception, Object context) { }
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene { }
    public enum NewSceneMode { Single = 0 }
}

namespace UnityEditor.Build
{
    public readonly struct NamedBuildTarget
    {
        private NamedBuildTarget(string name) { Name = name; }
        public string Name { get; }
        public static NamedBuildTarget Android => new NamedBuildTarget("Android");
    }
}

namespace UnityEditor.Build.Reporting
{
    public enum BuildResult
    {
        Unknown = 0,
        Succeeded = 1,
        Failed = 2,
        Cancelled = 3
    }

    public struct BuildSummary
    {
        public BuildResult result;
        public int totalErrors;
        public int totalWarnings;
        public ulong totalSize;
        public TimeSpan totalTime;
    }

    public sealed class BuildReport
    {
        public BuildSummary summary { get; set; } = new BuildSummary
        {
            result = BuildResult.Succeeded,
            totalTime = TimeSpan.Zero
        };
    }
}

namespace UnityEditor
{
    using System;
    using UnityEditor.Build;
    using UnityEditor.Build.Reporting;

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItem : Attribute
    {
        public MenuItem(string path) { }
    }

    public enum BuildTarget { Android = 0 }
    public enum BuildTargetGroup { Android = 0 }
    public enum BuildOptions { None = 0 }
    public enum ScriptingImplementation { Mono2x = 0, IL2CPP = 1 }
    [Flags]
    public enum AndroidArchitecture { None = 0, ARM64 = 1 }
    public enum AndroidBuildSystem { Gradle = 0 }

    public sealed class BuildPlayerOptions
    {
        public string[] scenes { get; set; } = Array.Empty<string>();
        public string locationPathName { get; set; }
        public BuildTarget target { get; set; }
        public BuildTargetGroup targetGroup { get; set; }
        public BuildOptions options { get; set; }
    }

    public sealed class EditorBuildSettingsScene
    {
        public EditorBuildSettingsScene(string path, bool enabled)
        {
            this.path = path;
            this.enabled = enabled;
        }
        public string path { get; set; }
        public bool enabled { get; set; }
    }

    public static class EditorBuildSettings
    {
        public static EditorBuildSettingsScene[] scenes { get; set; } = Array.Empty<EditorBuildSettingsScene>();
    }

    public static class BuildPipeline
    {
        public static bool IsBuildTargetSupported(BuildTargetGroup group, BuildTarget target) => true;
        public static BuildReport BuildPlayer(BuildPlayerOptions options) => new BuildReport();
    }

    public static class EditorUserBuildSettings
    {
        public static BuildTarget activeBuildTarget { get; set; } = BuildTarget.Android;
        public static bool buildAppBundle { get; set; }
        public static AndroidBuildSystem androidBuildSystem { get; set; }
        public static bool SwitchActiveBuildTarget(BuildTargetGroup group, BuildTarget target)
        {
            activeBuildTarget = target;
            return true;
        }
    }

    public static class PlayerSettings
    {
        private static string applicationIdentifier = "com.unjuno.phraselayer";
        private static ScriptingImplementation scriptingBackend = ScriptingImplementation.IL2CPP;

        public static void SetApplicationIdentifier(NamedBuildTarget target, string value) => applicationIdentifier = value;
        public static string GetApplicationIdentifier(NamedBuildTarget target) => applicationIdentifier;
        public static void SetScriptingBackend(NamedBuildTarget target, ScriptingImplementation value) => scriptingBackend = value;
        public static ScriptingImplementation GetScriptingBackend(NamedBuildTarget target) => scriptingBackend;

        public static class Android
        {
            public static AndroidArchitecture targetArchitectures { get; set; } = AndroidArchitecture.ARM64;
        }
    }

    public static class AssetDatabase
    {
        public static void Refresh() { }
        public static void SaveAssets() { }
        public static T LoadAssetAtPath<T>(string path) where T : UnityEngine.Object => null;
        public static UnityEngine.Object LoadMainAssetAtPath(string path) => null;
    }

    public static class EditorUtility
    {
        public static void SetDirty(UnityEngine.Object target) { }
    }

    public static class EditorApplication
    {
        public static void Exit(int code) { }
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
        public static bool SaveOpenScenes() => true;
    }
}

namespace Unity.InferenceEngine
{
    using UnityEngine;

    public enum BackendType
    {
        GPUCompute = 0,
        CPU = 1
    }

    public enum DataType
    {
        Float = 0,
        Int = 1,
        Bool = 2
    }

    public sealed class ModelAsset : Object { }

    public sealed class ModelInput
    {
        public string name = string.Empty;
        public DataType dataType;
    }

    public sealed class ModelOutput
    {
        public string name = string.Empty;
    }

    public sealed class Model
    {
        public List<ModelInput> inputs { get; } = new List<ModelInput>();
        public List<ModelOutput> outputs { get; } = new List<ModelOutput>();
    }

    public static class ModelLoader
    {
        public static Model Load(ModelAsset modelAsset) => new Model();
    }

    public readonly struct TensorShape
    {
        private readonly int[] dimensions;

        public TensorShape(params int[] dimensions)
        {
            this.dimensions = dimensions ?? Array.Empty<int>();
        }

        public int rank => dimensions?.Length ?? 0;
        public int this[int axis] => dimensions[axis];
        public override string ToString() => dimensions == null ? "[]" : "[" + string.Join(",", dimensions) + "]";
    }

    public abstract class Tensor : IDisposable
    {
        public abstract TensorShape shape { get; }
        public virtual void Dispose() { }
    }

    public sealed class Tensor<T> : Tensor
    {
        private readonly TensorShape tensorShape;
        private readonly T[] values;

        public Tensor(TensorShape shape, T[] values)
        {
            tensorShape = shape;
            this.values = values ?? Array.Empty<T>();
        }

        public override TensorShape shape => tensorShape;
        public Tensor<T> ReadbackAndClone() => new Tensor<T>(tensorShape, (T[])values.Clone());
        public T[] DownloadToArray() => (T[])values.Clone();
    }

    public sealed class Worker : IDisposable
    {
        public Worker(Model model, BackendType backendType) { }
        public void SetInput(string name, Tensor tensor) { }
        public void Schedule() { }
        public Tensor PeekOutput(string name) => null;
        public void CopyOutput(string name, ref Tensor tensor) { }
        public void Dispose() { }
    }
}

namespace PhraseLayer.Unity
{
    using UnityEngine;

    public sealed class PhraseLayerDemoBehaviour : Behaviour
    {
        public bool UsesTranslationEngineOverride => true;
        public MixedLanguagePlan CurrentPlan => null;
        public void SetTranslationEngine(ITranslationEngine translationEngine) { }
        public void SetAutoRunOnStart(bool enabled) { }
        public void SetSourceText(string text) { }
        public Task ReplanAsync() => Task.CompletedTask;
    }

    public static class UnityManagedMarianTokenizerLoader
    {
        public static bool TryCreateFromResources(
            string resourceRoot,
            out ITranslationTokenizer tokenizer,
            out string error)
        {
            tokenizer = null;
            error = "stub";
            return false;
        }
    }
}
