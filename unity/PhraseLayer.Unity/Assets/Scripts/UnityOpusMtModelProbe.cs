using System;
using System.Text;
using PhraseLayer.Core.Translation;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Import-time model identity probe for the measured OPUS-MT encoder/non-cached decoder pair.
    /// It validates names and Unity-visible data types only; it does not execute inference and therefore does
    /// not promote the exported ONNX pair to Quest-compatible status.
    /// </summary>
    public sealed class UnityOpusMtModelProbeBehaviour : MonoBehaviour
    {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        [SerializeField] private ModelAsset encoderModel = null;
        [SerializeField] private ModelAsset decoderModel = null;
        [SerializeField] private string lastReport = string.Empty;

        public string LastReport => lastReport;
        public bool IsSupported => true;

        public string ProbeModels()
        {
            lastReport = UnityOpusMtModelProbe.ValidateAndBuildReport(encoderModel, decoderModel);
            Debug.Log(lastReport, this);
            return lastReport;
        }
#else
        [SerializeField] private string lastReport =
            "OPUS-MT model probe disabled: expected com.unity.ai.inference in [2.2.1,2.3.0).";

        public string LastReport => lastReport;
        public bool IsSupported => false;
#endif
    }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
    public static class UnityOpusMtModelProbe
    {
        public static string ValidateAndBuildReport(ModelAsset encoderAsset, ModelAsset decoderAsset)
        {
            if (encoderAsset == null) throw new ArgumentNullException(nameof(encoderAsset));
            if (decoderAsset == null) throw new ArgumentNullException(nameof(decoderAsset));

            var encoder = ModelLoader.Load(encoderAsset);
            var decoder = ModelLoader.Load(decoderAsset);

            ValidateInputs(
                encoder,
                "encoder",
                new[] { "input_ids", "attention_mask" },
                new[] { DataType.Int, DataType.Int });
            RequireOutput(encoder, "encoder", "last_hidden_state");

            ValidateInputs(
                decoder,
                "decoder",
                new[] { "encoder_attention_mask", "input_ids", "encoder_hidden_states" },
                new[] { DataType.Int, DataType.Int, DataType.Float });
            RequireOutput(decoder, "decoder", "logits");

            var report = new StringBuilder(512);
            report.AppendLine("PhraseLayer OPUS-MT Unity import probe");
            report.AppendLine(OpusMtEnJapMeasuredOnnxContract.BuildReport());
            AppendModel(report, "encoder", encoder);
            AppendModel(report, "decoder", decoder);
            report.AppendLine("status=import-contract-pass runtime-execution=unverified quest=unverified");
            return report.ToString();
        }

        private static void ValidateInputs(
            Model model,
            string label,
            string[] expectedNames,
            DataType[] expectedTypes)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (expectedNames == null) throw new ArgumentNullException(nameof(expectedNames));
            if (expectedTypes == null) throw new ArgumentNullException(nameof(expectedTypes));
            if (expectedNames.Length != expectedTypes.Length)
                throw new ArgumentException("Expected input names/types length mismatch.");
            if (model.inputs.Count != expectedNames.Length)
            {
                throw new InvalidOperationException(
                    label + " input count mismatch: expected " + expectedNames.Length + " actual " + model.inputs.Count);
            }

            for (var index = 0; index < expectedNames.Length; index++)
            {
                var input = model.inputs[index];
                if (!string.Equals(input.name, expectedNames[index], StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        label + " input[" + index + "] name mismatch: expected " + expectedNames[index] +
                        " actual " + input.name);
                }
                if (input.dataType != expectedTypes[index])
                {
                    throw new InvalidOperationException(
                        label + " input[" + index + "] data type mismatch: expected " + expectedTypes[index] +
                        " actual " + input.dataType);
                }
            }
        }

        private static void RequireOutput(Model model, string label, string expectedName)
        {
            for (var index = 0; index < model.outputs.Count; index++)
            {
                if (string.Equals(model.outputs[index].name, expectedName, StringComparison.Ordinal))
                    return;
            }
            throw new InvalidOperationException(label + " output is missing: " + expectedName);
        }

        private static void AppendModel(StringBuilder report, string label, Model model)
        {
            report.Append(label).Append(" producer=").AppendLine(model.ProducerName ?? string.Empty);
            for (var index = 0; index < model.inputs.Count; index++)
            {
                var input = model.inputs[index];
                report.Append(label).Append(" input[").Append(index).Append("] ")
                    .Append(input.name ?? string.Empty)
                    .Append(" dtype=").Append(input.dataType)
                    .Append(" shape=").Append(input.shape)
                    .AppendLine();
            }
            for (var index = 0; index < model.outputs.Count; index++)
            {
                var output = model.outputs[index];
                report.Append(label).Append(" output[").Append(index).Append("] ")
                    .Append(output.name ?? string.Empty)
                    .AppendLine();
            }
        }
    }
#endif
}
