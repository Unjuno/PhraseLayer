using System;
using System.Collections.Generic;
using UnityEngine;

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

    public sealed class ModelInput
    {
        public string name { get; set; } = string.Empty;
        public int index { get; set; }
        public DataType dataType { get; set; }
        public TensorShape shape { get; set; } = new TensorShape(1);
    }

    public sealed class ModelOutput
    {
        public string name { get; set; } = string.Empty;
        public int index { get; set; }
    }

    public sealed class Model
    {
        public string ProducerName { get; set; } = string.Empty;
        public List<ModelInput> inputs { get; } = new List<ModelInput> { new ModelInput() };
        public List<ModelOutput> outputs { get; } = new List<ModelOutput> { new ModelOutput() };
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

        public TensorShape(int d0, int d1, int d2, int d3)
        {
            dimensions = new[] { d0, d1, d2, d3 };
        }

        public int rank => dimensions == null ? 0 : dimensions.Length;
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

        public Tensor(TensorShape shape, T[] data)
        {
            tensorShape = shape;
            values = data ?? Array.Empty<T>();
        }

        public override TensorShape shape => tensorShape;
        public Tensor<T> ReadbackAndClone() => new Tensor<T>(tensorShape, DownloadToArray());
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

        public Worker Schedule(Tensor input)
        {
            output = input;
            return this;
        }

        public Tensor PeekOutput() => output;
        public void Dispose() { }
    }
}
