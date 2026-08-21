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

        public List<Input> inputs = new List<Input>
        {
            new Input
            {
                name = string.Empty,
                index = 0,
                dataType = DataType.Float,
                shape = new DynamicTensorShape("(1)")
            }
        };

        public List<Output> outputs = new List<Output>
        {
            new Output { name = string.Empty, index = 0 }
        };
    }

    public static class ModelLoader
    {
        public static Model Load(ModelAsset asset) => new Model();
    }

    // Public API fidelity matters here because this file exists specifically to catch compile-time drift before UBA.
    [Serializable]
    public struct TensorShape
    {
        private int[] dimensions;

        public TensorShape(int d0) { dimensions = new[] { d0 }; }
        public TensorShape(int d0, int d1) { dimensions = new[] { d0, d1 }; }
        public TensorShape(int d0, int d1, int d2) { dimensions = new[] { d0, d1, d2 }; }
        public TensorShape(int d0, int d1, int d2, int d3) { dimensions = new[] { d0, d1, d2, d3 }; }

        public int rank => dimensions == null ? 0 : dimensions.Length;
        public int length
        {
            get
            {
                if (dimensions == null || dimensions.Length == 0) return 1;
                var total = 1;
                for (var i = 0; i < dimensions.Length; i++) total *= dimensions[i];
                return total;
            }
        }

        public int this[int axis]
        {
            get
            {
                var resolved = ResolveAxis(axis);
                return dimensions[resolved];
            }
            set
            {
                var resolved = ResolveAxis(axis);
                dimensions[resolved] = value;
            }
        }

        private int ResolveAxis(int axis)
        {
            var resolved = axis < 0 ? rank + axis : axis;
            if (resolved < 0 || resolved >= rank) throw new IndexOutOfRangeException();
            return resolved;
        }

        public override string ToString() => dimensions == null ? "[]" : "[" + string.Join(",", dimensions) + "]";
    }

    public abstract class Tensor : IDisposable
    {
        public abstract TensorShape shape { get; }
        public virtual Tensor ReadbackAndClone() => CloneForReadback();
        protected abstract Tensor CloneForReadback();
        public virtual void Dispose() { }
    }

    public sealed class Tensor<T> : Tensor where T : unmanaged
    {
        private readonly TensorShape tensorShape;
        private readonly T[] values;

        public Tensor(TensorShape shape, T[] data)
        {
            tensorShape = shape;
            values = data ?? Array.Empty<T>();
        }

        public override TensorShape shape => tensorShape;

        protected override Tensor CloneForReadback() => new Tensor<T>(tensorShape, DownloadToArray());

        public new Tensor<T> ReadbackAndClone() => new Tensor<T>(tensorShape, DownloadToArray());

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

        public void Schedule(Tensor input)
        {
            output = input;
        }

        public Tensor PeekOutput() => output;
        public void Dispose() { }
    }
}
