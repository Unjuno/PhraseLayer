using System;
using System.Collections.Generic;
using PhraseLayer.Core.Translation;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Scene-facing preflight for the three-model Marian ONNX export. This validates imported Unity ModelAsset
    /// input/output names against the Core graph contract and the exact inputs the current backend can bind before
    /// a translation backend allocates Workers.
    /// </summary>
    public sealed class UnityMarianOnnxContractProbeBehaviour : MonoBehaviour
    {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        [SerializeField] private ModelAsset encoderModel = null;
        [SerializeField] private ModelAsset decoderModel = null;
        [SerializeField] private ModelAsset decoderWithPastModel = null;
        [SerializeField] private string lastReport = string.Empty;

        public ModelAsset EncoderModel
        {
            get => encoderModel;
            set => encoderModel = value;
        }

        public ModelAsset DecoderModel
        {
            get => decoderModel;
            set => decoderModel = value;
        }

        public ModelAsset DecoderWithPastModel
        {
            get => decoderWithPastModel;
            set => decoderWithPastModel = value;
        }

        public bool IsSupported => true;
        public string LastReport => lastReport;

        public string ProbeBundle()
        {
            var report = UnityMarianOnnxContractProbe.ValidateBundle(
                encoderModel,
                decoderModel,
                decoderWithPastModel);
            lastReport = report.ToString();
            Debug.Log(lastReport, this);
            return lastReport;
        }
#else
        [SerializeField] private string lastReport =
            "Marian ONNX contract probe disabled: expected com.unity.ai.inference in [2.2.1,2.3.0).";

        public bool IsSupported => false;
        public string LastReport => lastReport;

        public string ProbeBundle()
        {
            Debug.Log(lastReport);
            return lastReport;
        }
#endif
    }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
    public static class UnityMarianOnnxContractProbe
    {
        public static MarianOnnxBundleContractReport ValidateBundle(
            ModelAsset encoderModelAsset,
            ModelAsset decoderModelAsset,
            ModelAsset decoderWithPastModelAsset)
        {
            if (encoderModelAsset == null) throw new ArgumentNullException(nameof(encoderModelAsset));
            if (decoderModelAsset == null) throw new ArgumentNullException(nameof(decoderModelAsset));
            if (decoderWithPastModelAsset == null) throw new ArgumentNullException(nameof(decoderWithPastModelAsset));

            var report = OpusMtEnJaMarianOnnxContract.ValidateBundle(
                BuildSignature("encoder_model.onnx", ModelLoader.Load(encoderModelAsset)),
                BuildSignature("decoder_model.onnx", ModelLoader.Load(decoderModelAsset)),
                BuildSignature("decoder_with_past_model.onnx", ModelLoader.Load(decoderWithPastModelAsset)));
            OpusMtEnJaMarianOnnxExecutionContract.ValidateSupportedInputs(report);
            return report;
        }

        private static MarianOnnxGraphSignature BuildSignature(string graphName, Model model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            var inputs = new List<MarianOnnxTensorSignature>(model.inputs.Count);
            for (var index = 0; index < model.inputs.Count; index++)
            {
                var input = model.inputs[index];
                inputs.Add(
                    new MarianOnnxTensorSignature(
                        input.name,
                        ConvertDataType(input.dataType),
                        rank: null));
            }

            // Unity Inference Engine's static Model output metadata does not expose output dtype/shape here.
            // Runtime execution must validate concrete Tensor<T> and shapes before cache reuse.
            var outputs = new List<MarianOnnxTensorSignature>(model.outputs.Count);
            for (var index = 0; index < model.outputs.Count; index++)
            {
                var output = model.outputs[index];
                outputs.Add(
                    new MarianOnnxTensorSignature(
                        output.name,
                        MarianOnnxTensorElementType.Unknown,
                        rank: null));
            }

            return new MarianOnnxGraphSignature(graphName, inputs, outputs);
        }

        private static MarianOnnxTensorElementType ConvertDataType(DataType dataType)
        {
            var value = dataType.ToString();
            if (value.IndexOf("Int", StringComparison.OrdinalIgnoreCase) >= 0)
                return MarianOnnxTensorElementType.Integer;
            if (value.IndexOf("Float", StringComparison.OrdinalIgnoreCase) >= 0)
                return MarianOnnxTensorElementType.Float;
            if (value.IndexOf("Bool", StringComparison.OrdinalIgnoreCase) >= 0)
                return MarianOnnxTensorElementType.Boolean;
            return MarianOnnxTensorElementType.Unknown;
        }
    }
#endif
}
