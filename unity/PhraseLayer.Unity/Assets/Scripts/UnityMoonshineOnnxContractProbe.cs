using System;
using System.Collections.Generic;
using PhraseLayer.Core.Audio;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
    /// <summary>
    /// Converts Unity Inference Engine's static model metadata into the runtime-neutral
    /// Moonshine v1 positional ABI contract before Workers are allocated.
    /// </summary>
    public static class UnityMoonshineOnnxContractProbe
    {
        public static MoonshineOnnxBundleContractReport ValidateBundle(
            ModelAsset preprocessModelAsset,
            ModelAsset encoderModelAsset,
            ModelAsset uncachedDecoderModelAsset,
            ModelAsset cachedDecoderModelAsset)
        {
            if (preprocessModelAsset == null) throw new ArgumentNullException(nameof(preprocessModelAsset));
            if (encoderModelAsset == null) throw new ArgumentNullException(nameof(encoderModelAsset));
            if (uncachedDecoderModelAsset == null) throw new ArgumentNullException(nameof(uncachedDecoderModelAsset));
            if (cachedDecoderModelAsset == null) throw new ArgumentNullException(nameof(cachedDecoderModelAsset));

            return MoonshineTinyV1OnnxContract.ValidateBundle(
                BuildSignature("preprocess.onnx", ModelLoader.Load(preprocessModelAsset)),
                BuildSignature("encode.onnx", ModelLoader.Load(encoderModelAsset)),
                BuildSignature("uncached_decode.onnx", ModelLoader.Load(uncachedDecoderModelAsset)),
                BuildSignature("cached_decode.onnx", ModelLoader.Load(cachedDecoderModelAsset)));
        }

        private static MoonshineOnnxGraphSignature BuildSignature(string graphName, Model model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var inputs = new List<MoonshineOnnxTensorSignature>(model.inputs.Count);
            for (var index = 0; index < model.inputs.Count; index++)
            {
                var input = model.inputs[index];
                inputs.Add(new MoonshineOnnxTensorSignature(input.name, ConvertDataType(input.dataType), rank: null));
            }

            // Inference Engine 2.2.1 does not expose static output dtype/rank through Model.Output.
            // Concrete output tensor type/shape is checked by the execution backend before reuse.
            var outputs = new List<MoonshineOnnxTensorSignature>(model.outputs.Count);
            for (var index = 0; index < model.outputs.Count; index++)
            {
                outputs.Add(new MoonshineOnnxTensorSignature(
                    model.outputs[index].name,
                    MoonshineOnnxTensorElementType.Unknown,
                    rank: null));
            }
            return new MoonshineOnnxGraphSignature(graphName, inputs, outputs);
        }

        private static MoonshineOnnxTensorElementType ConvertDataType(DataType dataType)
        {
            var value = dataType.ToString();
            if (value.IndexOf("Int", StringComparison.OrdinalIgnoreCase) >= 0)
                return MoonshineOnnxTensorElementType.Integer;
            if (value.IndexOf("Float", StringComparison.OrdinalIgnoreCase) >= 0)
                return MoonshineOnnxTensorElementType.Float;
            if (value.IndexOf("Bool", StringComparison.OrdinalIgnoreCase) >= 0)
                return MoonshineOnnxTensorElementType.Boolean;
            return MoonshineOnnxTensorElementType.Unknown;
        }
    }
#else
    public static class UnityMoonshineOnnxContractProbe
    {
        public static bool IsSupported => false;
    }
#endif
}
