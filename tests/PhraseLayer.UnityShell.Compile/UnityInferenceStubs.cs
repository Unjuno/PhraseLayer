using System;
using System.Collections.Generic;

namespace Unity.InferenceEngine
{
    public enum BackendType
    {
        CPU = 0,
        GPUCompute = 1,
        GPUPixel = 2
    }

    public enum DataType
    {
        Float = 0,
        Int = 1
    }

    public sealed class ModelAsset : UnityEngine.Object { }

    /// <summary>
    /// Narrow compile stub matching the public Inference Engine 2.2.1 distinction between
    /// model-input DynamicTensorShape and runtime TensorShape. This is intentionally not a
    /// functional shape implementation; it exists to keep host preflight signatures honest.
    /// </summary>
    public readonly struct DynamicTensorShape
    {
        private readonly string description;

        public DynamicTensorShape(string description)
        {
            this.description = description ?? string.Empty;
        }

        public override string ToString() => description ?? string.Empty;
    }

    public sealed class Model
    {
        public string ProducerName = string.Empty;

        public struct Input
        {
            public string name;
            public int index;
            public DataType dataType;
            public DynamicTensorShape shape;
        }

        public struct Output
        {
            public string name;
            public int index;
        }

        public List<Input> inputs { get; } = new List<Input>
        {
            new Input
            {
                name = string.Empty,
                index = 0,
                dataType = DataType.Float,
                shape = new DynamicTensorShape("(1)")
            }
        };

        public List<Output> outputs { get; } = new List<Output>
        {
            new Output { name = string.Empty, index = 0 }
        };
    }

    public static class ModelLoader
    {
        public static Model Load(ModelAsset asset) => new Model();
    }

    public readonly struct TensorShape
    {
        private readonly int[] dimensions;

        public TensorShape(int d0)
        {
            dimensions = new[] { d0 };
        }

        public TensorShape(int d0, int d1)
        {
            dimensions = new[] { d0, d1 };
        }

        public TensorShape(int d0, int d1, int d2)
        {
            dimensions = new[] { d0, d1, d2 };
        }

        public TensorShape(int d0, int d1, int d2, int d3)
        {
            dimensions = new[] { d0, d1, d2, d3 };
        }

        public int rank => dimensions == null ? 0 : dimensions.Length;
        public int this[int axis] => dimensions[axis];
        public override string ToString() => dimensions == null ? "[]" : "[" + string.Join(",", dimensions) + "]";
    }

    /// <summary>
    /// Inference Engine 2.2.1 declares ReadbackAndClone on non-generic Tensor and returns Tensor.
    /// Keeping that return type exact is important: returning Tensor&lt;T&gt; here previously hid a real
    /// Unity compile error when runtime code called DownloadToArray without casting the CPU clone.
    /// </summary>
    public abstract class Tensor : IDisposable
    {
        public abstract TensorShape shape { get; }

        public Tensor ReadbackAndClone()
        {
            return CloneForReadback();
        }

        protected abstract Tensor CloneForReadback();

        public virtual void Dispose() { }
    }

    public sealed class Tensor<T> : Tensor
    {
        private readonly TensorShape tensorShape;
        private readonly T[] values;

        public Tensor(TensorShape shape, T[] data)
        {
            tensorShape = shape;
            values = data ?? Array.Empty<T>();
        }

        public override TensorShape shape => tensorShape;

        protected override Tensor CloneForReadback()
        {
            return new Tensor<T>(tensorShape, DownloadToArray());
        }

        public T[] DownloadToArray()
        {
            var copy = new T[values.Length];
            Array.Copy(values, copy, values.Length);
            return copy;
        }
    }

    public sealed class Worker : IDisposable
    {
        private Tensor output;

        public Worker(Model model, BackendType backendType) { }

        // Inference Engine 2.2.1 Schedule is void. Keeping this exact prevents fluent-call
        // assumptions in host preflight that would fail in the real Unity package.
        public void Schedule(Tensor input)
        {
            output = input;
        }

        public Tensor PeekOutput() => output;
        public void Dispose() { }
    }
}
