using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object { }

    public class Texture : Object
    {
        public int width { get; set; }
        public int height { get; set; }
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

    public enum TensorLayout
    {
        NCHW = 0,
        NHWC = 1
    }

    public enum CoordOrigin
    {
        TopLeft = 0,
        BottomLeft = 1
    }

    public enum ChannelSwizzle
    {
        RGBA = 0,
        BGRA = 1
    }

    public sealed class ModelAsset : Object { }

    public sealed class ModelInput
    {
        public DataType dataType;
    }

    public sealed class ModelOutput { }

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
    }

    public abstract class Tensor : IDisposable
    {
        public abstract TensorShape shape { get; }
        public virtual void Dispose() { }
    }

    public sealed class Tensor<T> : Tensor where T : unmanaged
    {
        private readonly TensorShape tensorShape;

        public Tensor(TensorShape shape)
        {
            tensorShape = shape;
        }

        public override TensorShape shape => tensorShape;
        public Tensor<T> ReadbackAndClone() => new Tensor<T>(tensorShape);
        public T[] DownloadToArray() => Array.Empty<T>();
    }

    public struct TextureTransform
    {
        public TextureTransform SetTensorLayout(TensorLayout tensorLayout) => this;
        public TextureTransform SetCoordOrigin(CoordOrigin coordOrigin) => this;
        public TextureTransform SetChannelSwizzle(ChannelSwizzle channelSwizzle) => this;
    }

    public static class TextureConverter
    {
        public static void ToTensor(Texture texture, Tensor<float> tensor, TextureTransform transform = default) { }
    }

    public sealed class FunctionalTensor
    {
        public static FunctionalTensor operator -(FunctionalTensor left, FunctionalTensor right) => new FunctionalTensor();
        public static FunctionalTensor operator /(FunctionalTensor left, FunctionalTensor right) => new FunctionalTensor();
    }

    public sealed class FunctionalGraph
    {
        public FunctionalTensor AddInput(Model model, int index, string name = null) => new FunctionalTensor();
        public void AddOutputs(params FunctionalTensor[] outputs) { }
        public Model Compile() => new Model();
    }

    public static class Functional
    {
        public static FunctionalTensor Constant(TensorShape shape, float[] values) => new FunctionalTensor();
        public static FunctionalTensor[] Forward(Model model, params FunctionalTensor[] inputs) => Array.Empty<FunctionalTensor>();
    }

    public sealed class Worker : IDisposable
    {
        public Worker(Model model, BackendType backendType) { }
        public void Schedule(Tensor input) { }
        public Tensor PeekOutput() => null;
        public void Dispose() { }
    }
}
