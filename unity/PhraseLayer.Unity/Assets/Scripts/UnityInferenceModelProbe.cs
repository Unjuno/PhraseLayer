using System;
using System.Text;
using UnityEngine;

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
using Unity.InferenceEngine;
#endif

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Lightweight model-inspection helper for the pinned Unity Inference Engine 2.2.x API surface.
    /// This intentionally does not execute inference; it only validates that Unity can import/load the ModelAsset
    /// and exposes the model metadata we need before wiring PP-OCR preprocessing and postprocessing.
    /// </summary>
    public sealed class UnityInferenceModelProbeBehaviour : MonoBehaviour
    {
#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
        [SerializeField] private ModelAsset modelAsset = null;
        [SerializeField] private string lastReport = string.Empty;

        public ModelAsset ModelAsset
        {
            get => modelAsset;
            set => modelAsset = value;
        }

        public string LastReport => lastReport;
        public bool IsSupported => true;

        public string ProbeModel()
        {
            lastReport = UnityInferenceModelProbe.BuildReport(modelAsset);
            Debug.Log(lastReport, this);
            return lastReport;
        }
#else
        [SerializeField] private string lastReport =
            "Unity Inference Engine probe disabled: expected com.unity.ai.inference in [2.2.1,2.3.0).";

        public string LastReport => lastReport;
        public bool IsSupported => false;

        public string ProbeModel()
        {
            Debug.LogWarning(lastReport, this);
            return lastReport;
        }
#endif
    }

#if PHRASELAYER_UNITY_AI_INFERENCE_2_2
    public static class UnityInferenceModelProbe
    {
        public static string BuildReport(ModelAsset modelAsset)
        {
            if (modelAsset == null) throw new ArgumentNullException(nameof(modelAsset));

            var model = ModelLoader.Load(modelAsset);
            var report = new StringBuilder(512);

            report.AppendLine("PhraseLayer Unity Inference model probe");
            report.Append("producer: ").AppendLine(model.ProducerName ?? string.Empty);
            report.Append("inputs: ").AppendLine(model.inputs.Count.ToString());

            for (var index = 0; index < model.inputs.Count; index++)
            {
                var input = model.inputs[index];
                report
                    .Append("input[").Append(index).Append("] ")
                    .Append("name=").Append(input.name ?? string.Empty)
                    .Append(" index=").Append(input.index)
                    .Append(" dtype=").Append(input.dataType)
                    .Append(" shape=").Append(input.shape)
                    .AppendLine();
            }

            report.Append("outputs: ").AppendLine(model.outputs.Count.ToString());
            for (var index = 0; index < model.outputs.Count; index++)
            {
                var output = model.outputs[index];
                report
                    .Append("output[").Append(index).Append("] ")
                    .Append("name=").Append(output.name ?? string.Empty)
                    .Append(" index=").Append(output.index)
                    .AppendLine();
            }

            report.AppendLine(
                "note: output shape/dtype are resolved after execution; this metadata probe intentionally does not allocate a Worker or Tensor.");

            return report.ToString();
        }
    }
#endif
}
