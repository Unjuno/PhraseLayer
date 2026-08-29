using System;
using System.Collections.Generic;
using PhraseLayer.Core.Translation;

namespace UnityEngine
{
    public class Object { }
    public class Component : Object { }
    public class Behaviour : Component { public bool enabled { get; set; } = true; }
    public class MonoBehaviour : Behaviour { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void Log(object message, Object context) { }
        public static void LogError(object message, Object context) { }
        public static void LogException(Exception exception, Object context) { }
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
        public void SetTranslationEngine(ITranslationEngine translationEngine) { }
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
