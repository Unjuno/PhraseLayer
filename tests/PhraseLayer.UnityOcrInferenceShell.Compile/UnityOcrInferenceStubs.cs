using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        public string name { get; set; }
        public HideFlags hideFlags { get; set; }
        public static void Destroy(Object obj) { }
        public static void DestroyImmediate(Object obj) { }
    }

    public enum HideFlags
    {
        HideAndDontSave = 0
    }

    public class Texture : Object
    {
        public int width { get; set; }
        public int height { get; set; }
    }

    public sealed class Shader : Object { }

    public sealed class Material : Object
    {
        public Material(Shader shader) { }
        public void SetFloat(string name, float value) { }
    }

    public static class Resources
    {
        public static T Load<T>(string path) where T : Object => null;
    }

    public enum RenderTextureFormat
    {
        ARGBHalf = 0
    }

    public enum RenderTextureReadWrite
    {
        Linear = 0
    }

    public enum FilterMode
    {
        Bilinear = 0,
        Point = 1
    }

    public enum TextureWrapMode
    {
        Clamp = 0
    }

    public sealed class RenderTexture : Texture
    {
        public FilterMode filterMode { get; set; }
        public TextureWrapMode wrapMode { get; set; }

        public static RenderTexture GetTemporary(
            int width,
            int height,
            int depthBuffer,
            RenderTextureFormat format,
            RenderTextureReadWrite readWrite)
        {
            return new RenderTexture { width = width, height = height };
        }

        public static void ReleaseTemporary(RenderTexture texture) { }
    }

    public static class Graphics
    {
        public static void Blit(Texture source, RenderTexture destination, Material material, int pass) { }
    }

    public enum TextureFormat
    {
        RGBA32 = 0
    }

    public sealed class Texture2D : Texture
    {
        public FilterMode filterMode { get; set; }
        public TextureWrapMode wrapMode { get; set; }

        public Texture2D(int width, int height, TextureFormat format, bool mipChain, bool linear)
        {
            this.width = width;
            this.height = height;
        }

        public void SetPixels32(Color32[] pixels) { }
        public void Apply(bool updateMipmaps, bool makeNoLongerReadable) { }
    }

    public readonly struct Color32
    {
        public Color32(byte r, byte g, byte b, byte a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public readonly byte r;
        public readonly byte g;
        public readonly byte b;
        public readonly byte a;
    }

    public readonly struct Vector2Int
    {
        public Vector2Int(int x, int y)
        {
            this.x = x;
            this.y = y;
        }

        public readonly int x;
        public readonly int y;
        public override string ToString() => "(" + x + ", " + y + ")";
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogException(Exception exception) { }
    }
}

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItem : Attribute
    {
        public MenuItem(string itemName) { }
    }

    public static class EditorApplication
    {
        public static void Exit(int code) { }
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
        public static FunctionalTensor ArgMax(FunctionalTensor input, int dim = 0, bool keepdim = false) => new FunctionalTensor();
        public static FunctionalTensor ReduceMax(FunctionalTensor input, int dim, bool keepdim = false) => new FunctionalTensor();
    }

    public sealed class Worker : IDisposable
    {
        public Worker(Model model, BackendType backendType) { }
        public void Schedule(Tensor input) { }
        public Tensor PeekOutput() => null;
        public Tensor PeekOutput(int index) => null;
        public void Dispose() { }
    }
}
