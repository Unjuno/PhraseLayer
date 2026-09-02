#!/usr/bin/env python3
"""Static contract for reducing PP-OCR recognizer CTC readback on GPU before live decoding."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/UnityPaddleOcrRecognizerRuntime.cs"
ENGINE = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/UnityPaddleOcrEngine.cs"
CORE_CONTRACT = ROOT / "src/PhraseLayer.Core/PaddleOcrRuntimeContract.cs"
PROBE = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerPaddleOcrRecognizerGpuReductionProbe.cs"
SHELL = ROOT / "tools/unity/verify-recognizer-gpu-reduction.sh"
COMMON_OCR_SHELL = ROOT / "tools/unity/verify-local-ocr-inference.sh"
GUARDED_CSPROJ = ROOT / "tests/PhraseLayer.UnityOcrInferenceShell.Compile/PhraseLayer.UnityOcrInferenceShell.Compile.csproj"
MANIFEST = ROOT / "unity/PhraseLayer.Unity/Packages/manifest.json"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def forbid(text: str, fragment: str, label: str) -> None:
    if fragment in text:
        raise GateError(f"{label} contains forbidden marker: {fragment}")


def validate() -> dict[str, object]:
    runtime = RUNTIME.read_text(encoding="utf-8")
    engine = ENGINE.read_text(encoding="utf-8")
    core = CORE_CONTRACT.read_text(encoding="utf-8")
    probe = PROBE.read_text(encoding="utf-8")
    shell = SHELL.read_text(encoding="utf-8")
    common_ocr_shell = COMMON_OCR_SHELL.read_text(encoding="utf-8")
    guarded = GUARDED_CSPROJ.read_text(encoding="utf-8")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))

    for fragment in (
        "public sealed class PaddleRecognizerReducedOutput",
        "public int[] ClassIndices { get; }",
        "public float[] MaxScores { get; }",
        "PaddleCtcGreedyDecoder.DecodeFromIndices(ClassIndices, MaxScores, characterDictionary)",
        "private readonly ModelAsset modelAsset",
        "private readonly Worker reducedOutputWorker",
        "public bool UsesGpuCtcReduction => true",
        "public bool RetainsFullOutputWorker => false",
        "BuildGpuReducedOutputModel(model)",
        "var outputs = Functional.Forward(sourceModel, input)",
        "Functional.ArgMax(probabilities, dim: -1, keepdim: false)",
        "Functional.ReduceMax(probabilities, dim: -1, keepdim: false)",
        "graph.AddOutputs(classIndices, maxScores, probabilities)",
        "public PaddleRecognizerReducedOutput ExecuteReduced(",
        "reducedOutputWorker.PeekOutput(0) as Tensor<int>",
        "reducedOutputWorker.PeekOutput(1) as Tensor<float>",
        "reducedOutputWorker.PeekOutput(2) as Tensor<float>",
        "var outputShape = CopyShape(probabilityTensor.shape)",
        "var indexCpu = indexTensor.ReadbackAndClone()",
        "var scoreCpu = scoreTensor.ReadbackAndClone()",
        "indexCpu.DownloadToArray()",
        "scoreCpu.DownloadToArray()",
        "using (var parityWorker = new Worker(ModelLoader.Load(modelAsset), backendType))",
        "parityWorker.Schedule(inputTensor)",
        "selectLastIndex=false",
    ):
        require(runtime, fragment, "recognizer GPU CTC reduction runtime")

    for forbidden in (
        "probabilityTensor.ReadbackAndClone()",
        "probabilityTensor.DownloadToArray()",
        "private readonly Worker fullOutputWorker",
    ):
        forbid(runtime, forbidden, "live recognizer reduced output path")

    for fragment in (
        "public bool UsesGpuRecognizerCtcReduction => recognizer.UsesGpuCtcReduction",
        "var recognizerOutput = recognizer.ExecuteReduced(",
        "PaddleOcrRuntimeContract.ValidateRecognizerReduced(",
        "recognizerOutput.ClassIndices",
        "recognizerOutput.MaxScores",
        "var decoded = recognizerOutput.Decode(characterDictionary)",
        "Recognizer probability matrices stay GPU-side in the live path",
    ):
        require(engine, fragment, "live Unity PP-OCR engine")
    forbid(engine, "var recognizerOutput = recognizer.Execute(\n", "live Unity PP-OCR engine")

    for fragment in (
        "public static PaddleRecognizerRuntimeContract ValidateRecognizerReduced(",
        "classIndices.Length != contract.TimeSteps",
        "maxScores.Length != contract.TimeSteps",
        "classIndex < 0 || classIndex >= contract.ClassCount",
        "float.IsNaN(score) || float.IsInfinity(score)",
        "ValidateRecognizerShape(outputShape, dictionaryTokenCount)",
    ):
        require(core, fragment, "Core reduced recognizer runtime contract")

    for fragment in (
        "PhraseLayerPaddleOcrRecognizerGpuReductionProbe",
        "runtime.UsesGpuCtcReduction",
        "var full = runtime.Execute(",
        "var reduced = runtime.ExecuteReduced(",
        "PaddleOcrRuntimeContract.ValidateRecognizer(",
        "PaddleOcrRuntimeContract.ValidateRecognizerReduced(",
        "ReduceOnCpu(",
        "if (score > bestScore)",
        "RequireReducedParity(expectedIndices, expectedScores, reduced)",
        "actual.ClassIndices[time] != expectedIndices[time]",
        "error > ScoreTolerance",
        "fullDecoded.Text, reducedDecoded.Text",
        "fullDecoded.EmittedTokenCount != reducedDecoded.EmittedTokenCount",
        "Math.Abs(fullDecoded.Confidence - reducedDecoded.Confidence) > ScoreTolerance",
        "PhraseLayer PP-OCR recognizer GPU reduction parity PASS",
        "public static void RunBatch()",
    ):
        require(probe, fragment, "real-Unity recognizer GPU reduction parity probe")

    for fragment in (
        "UNITY_EDITOR must point to the Unity 6000.0.66f2 Editor executable.",
        "recognizer.onnx",
        "ppocr_keys.txt",
        "Intentionally no -nographics",
        "PhraseLayerPaddleOcrRecognizerGpuReductionProbe.RunBatch",
        "full-matrix versus GPU ArgMax/ReduceMax CTC reduction parity",
    ):
        require(shell, fragment, "recognizer GPU reduction parity shell")
    if " -nographics" in shell or "\n  -nographics" in shell:
        raise GateError("recognizer GPU reduction parity shell must use a real graphics device")

    for fragment in (
        'bash "$ROOT/tools/unity/verify-recognizer-gpu-reduction.sh"',
        "Production recognition does not download the full [time,class] probability matrix",
        "recognizer GPU CTC reduction parity",
    ):
        require(common_ocr_shell, fragment, "shared real-Unity OCR host gate")

    for fragment in (
        "UnityPaddleOcrRecognizerRuntime.cs",
        "UnityPaddleOcrEngine.cs",
        "PhraseLayerPaddleOcrRecognizerGpuReductionProbe.cs",
        "PHRASELAYER_UNITY_AI_INFERENCE_2_2",
    ):
        require(guarded, fragment, "guarded PP-OCR compile project")

    if manifest.get("dependencies", {}).get("com.unity.ai.inference") != "2.2.1":
        raise GateError("recognizer GPU CTC reduction gate requires com.unity.ai.inference@2.2.1")

    return {
        "status": "pass",
        "inference_engine_version": "2.2.1",
        "full_matrix_path_retained_for_parity": True,
        "production_full_output_worker_retained": False,
        "parity_full_output_worker_temporary": True,
        "live_full_probability_matrix_cpu_readback": False,
        "live_argmax_gpu_reduction_required": True,
        "live_reduce_max_gpu_reduction_required": True,
        "first_index_on_ties_required": True,
        "real_unity_full_vs_reduced_parity_required": True,
        "shared_ocr_host_gate_chains_reduction_parity": True,
        "live_cpu_values_per_timestep": 2,
        "quest_execution_required": False,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
