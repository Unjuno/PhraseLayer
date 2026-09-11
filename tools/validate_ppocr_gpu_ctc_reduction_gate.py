#!/usr/bin/env python3
"""Static wiring checks for packed CTC. This tool never claims real Unity/GPU execution."""
from __future__ import annotations

import json
import re
import shlex
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/UnityPaddleOcrRecognizerRuntime.cs"
ENGINE = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/UnityPaddleOcrEngine.cs"
CORE_CONTRACT = ROOT / "src/PhraseLayer.Core/PaddleOcrRuntimeContract.cs"
PACKED_CORE = ROOT / "src/PhraseLayer.Core/PaddleCtcPackedOutput.cs"
PACKED_TESTS = ROOT / "tests/PhraseLayer.Core.Tests/PaddleCtcPackedOutputTests.cs"
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


def code_only(source: str) -> str:
    # Mask comments and strings before inspecting method structure/call counts. This is not a C# parser.
    pattern = r'@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|//[^\n]*|/\*[\s\S]*?\*/'
    return re.sub(pattern, lambda m: re.sub(r"[^\n]", " ", m.group()), source)


def method_body(source: str, name: str) -> str:
    code = code_only(source)
    match = re.search(r"\bpublic\s+(?:static\s+)?[\w<>]+\s+" + re.escape(name) + r"\s*\(", code)
    if match is None:
        raise GateError(f"missing method {name}")
    start = code.find("{", match.end())
    depth = 0
    for index in range(start, len(code)):
        depth += (code[index] == "{") - (code[index] == "}")
        if depth == 0:
            return code[start + 1:index]
    raise GateError(f"unterminated method {name}")


def validate_live_readback(runtime: str) -> None:
    body = method_body(runtime, "ExecuteReduced")
    calls = re.findall(r"\b(\w+)\s*\.\s*ReadbackAndClone\s*\(", body)
    if calls != ["packedTensor"]:
        raise GateError("live CTC must have exactly one explicit readback, from packedTensor")
    downloads = re.findall(r"\b(\w+)\s*\.\s*DownloadToArray\s*\(", body)
    if downloads != ["packedCpu"]:
        raise GateError("live CTC must download only the packed CPU clone")
    for fragment in ("ReadbackAndCloneAsync", "ReadbackRequest", "new Worker", "parityWorker"):
        forbid(body, fragment, "live CTC method")
    for fragment in (
        "CopyShape(probabilityTensor.shape)", "CopyShape(packedTensor.shape)",
        "PaddleCtcPackedOutput.ValidateShapes(outputShape, packedShape)",
        "PaddleCtcPackedOutput.Unpack(outputShape, packedShape, packedCpu.DownloadToArray())",
    ):
        require(body, fragment, "live packed ABI boundary")
    if body.index("ValidateShapes(") > body.index("ReadbackAndClone("):
        raise GateError("packed tensor shape must be checked before readback")


def validate_graphics_shell(shell: str) -> None:
    # Quoted and same-line arguments count; comments saying 'no -nographics' do not.
    tokens = shlex.split(shell.replace("\\\n", ""), comments=True, posix=True)
    if "-nographics" in tokens:
        raise GateError("GPU parity shell must use a real graphics device")


def validate() -> dict[str, object]:
    runtime = RUNTIME.read_text(encoding="utf-8")
    engine = ENGINE.read_text(encoding="utf-8")
    core = CORE_CONTRACT.read_text(encoding="utf-8")
    packed_core = PACKED_CORE.read_text(encoding="utf-8")
    tests = PACKED_TESTS.read_text(encoding="utf-8")
    probe = PROBE.read_text(encoding="utf-8")
    shell = SHELL.read_text(encoding="utf-8")
    common = COMMON_OCR_SHELL.read_text(encoding="utf-8")
    guarded = GUARDED_CSPROJ.read_text(encoding="utf-8")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    validate_live_readback(runtime)
    validate_graphics_shell(shell)
    for fragment in (
        "private readonly Worker reducedOutputWorker", "private readonly ModelAsset modelAsset",
        "UsesGpuCtcReduction => backendType == BackendType.GPUCompute", "RetainsFullOutputWorker => false",
        "ReducedReadbackOperationsPerCrop = 1", "PaddleCtcPackedOutput.ValuesPerTimestep",
        "BuildGpuReducedOutputModel(model)", "var outputs = Functional.Forward(sourceModel, input)",
        "PackReviewedCtcOutput(probabilities)", "graph.AddOutputs(packed, probabilities)",
        "using (var parityWorker = new Worker(ModelLoader.Load(modelAsset), backendType))",
        "parityWorker.Schedule(inputTensor)", "selectLastIndex=false",
    ):
        require(runtime, fragment, "recognizer runtime")
    packing = method_body(runtime, "PackReviewedCtcOutput")
    for fragment in (
        "Functional.ArgMax(probabilities, dim: -1, keepdim: false)",
        "Functional.ReduceMax(probabilities, dim: -1, keepdim: false)",
        "Functional.Concat(", "classIndices.Float().Unsqueeze(-1)", "maxScores.Unsqueeze(-1)",
    ):
        require(packing, fragment, "shared GPU packing operators")
    for fragment in ("private readonly Worker fullOutputWorker", "probabilityTensor.ReadbackAndClone()", "probabilityTensor.DownloadToArray()"):
        forbid(runtime, fragment, "production runtime")
    for fragment in (
        "public bool UsesGpuRecognizerCtcReduction => recognizer.UsesGpuCtcReduction",
        "public bool RetainsFullRecognizerOutputWorker => recognizer.RetainsFullOutputWorker",
        "var recognizerOutput = recognizer.ExecuteReduced(", "PaddleOcrRuntimeContract.ValidateRecognizerReduced(",
        "recognizerOutput.ClassIndices", "recognizerOutput.MaxScores", "var decoded = recognizerOutput.Decode(characterDictionary)",
    ):
        require(engine, fragment, "live OCR engine")
    forbid(engine, "var recognizerOutput = recognizer.Execute(\n", "live OCR engine")
    for fragment in ("classIndices.Length != contract.TimeSteps", "maxScores.Length != contract.TimeSteps", "classIndex < 0 || classIndex >= contract.ClassCount"):
        require(core, fragment, "dictionary/witness contract")
    for fragment in (
        "MaximumClassCount = 16777216", "packedShape[1] != probabilityShape[1]", "packedShape[2] != ValuesPerTimestep",
        "checked(timeSteps * ValuesPerTimestep)", "classValue >= classCount", "Math.Truncate((double)classValue)",
        "float.IsNaN(score) || float.IsInfinity(score) || score < 0f || score > 1f",
    ):
        require(packed_core, fragment, "Core packed contract")
    for fragment in ("EqualElementCountDoesNotAcceptTransposedPackedLayout", "InvalidClassValuesFailBeforeIntegerCast",
                     "InvalidScoresAreRejectedEvenOnBlankTimesteps", "ChecksBufferLengthOverflowBeforeAllocation"):
        require(tests, fragment, "packed regression tests")
    for fragment in (
        "RunSyntheticPackingProbe();", "PackReviewedCtcOutput(input)", "graph.AddInput(DataType.Float, shape)",
        "new[] { 64, 192, 321 }", "new[] { 0, 2, 2, 0, 2, 4, 4, 0 }", "new[] { classes - 1, 1 }",
        "runtime.CtcReadbackOperationsPerCrop != 1", "var full = runtime.Execute(", "var reduced = runtime.ExecuteReduced(",
        "RequireReducedParity(expectedIndices, expectedScores, reduced)", "actual.ClassIndices[time] != expectedIndices[time]",
        "error > ScoreTolerance", "fullDecoded.Text, reducedDecoded.Text",
        "fullDecoded.EmittedTokenCount != reducedDecoded.EmittedTokenCount",
        "Math.Abs(fullDecoded.Confidence - reducedDecoded.Confidence) > ScoreTolerance",
        "PhraseLayer PP-OCR recognizer GPU reduction parity PASS", "public static void RunBatch()",
    ):
        require(probe, fragment, "real Unity numerical probe")
    for fragment in ("recognizer.onnx", "ppocr_keys.txt", "PhraseLayerPaddleOcrRecognizerGpuReductionProbe.RunBatch"):
        require(shell, fragment, "real Unity shell")
    require(common, 'bash "$ROOT/tools/unity/verify-recognizer-gpu-reduction.sh"', "shared OCR host gate")
    for fragment in ("UnityPaddleOcrRecognizerRuntime.cs", "UnityPaddleOcrEngine.cs", "PhraseLayerPaddleOcrRecognizerGpuReductionProbe.cs", "PHRASELAYER_UNITY_AI_INFERENCE_2_2"):
        require(guarded, fragment, "guarded compile project")
    if manifest.get("dependencies", {}).get("com.unity.ai.inference") != "2.2.1":
        raise GateError("packed GPU CTC requires the reviewed Inference Engine 2.2.1 pin")
    return {"status": "pass", "scope": "static-wiring-only", "real_unity_execution_performed": False,
            "quest_execution_required": False, "live_cpu_values_per_timestep": 2,
            "explicit_output_readbacks_per_crop": 1, "packed_layout": "[1,time,2]",
            "actual_packed_shape_checked": True, "float32_exact_index_domain_checked": True,
            "blank_and_duplicate_scores_checked": True, "full_matrix_path_retained_for_parity": True,
            "production_full_output_worker_retained": False, "real_unity_full_vs_reduced_parity_required": True}

if __name__ == "__main__":
    print(json.dumps(validate(), sort_keys=True))
