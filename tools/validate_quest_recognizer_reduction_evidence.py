#!/usr/bin/env python3
"""Prevent Quest Read Mode evidence from claiming OCR PASS without the production reduced recognizer path."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OCR_SMOKE = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/QuestOcrSmokeTestBehaviour.cs"
ENGINE = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/UnityPaddleOcrEngine.cs"
RECOGNIZER = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/UnityPaddleOcrRecognizerRuntime.cs"
RUNNER = ROOT / "tools/run_quest_read_mode_smoke.py"
WORKFLOW = ROOT / ".github/workflows/quest3-read-mode-smoke.yml"
COMMON_OCR_GATE = ROOT / "tools/unity/verify-local-ocr-inference.sh"
REDUCTION_GATE = ROOT / "tools/unity/verify-recognizer-gpu-reduction.sh"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def forbid(text: str, fragment: str, label: str) -> None:
    if fragment in text:
        raise GateError(f"{label} contains forbidden marker: {fragment}")


def validate() -> dict[str, object]:
    ocr = OCR_SMOKE.read_text(encoding="utf-8")
    engine = ENGINE.read_text(encoding="utf-8")
    recognizer = RECOGNIZER.read_text(encoding="utf-8")
    runner = RUNNER.read_text(encoding="utf-8")
    workflow = WORKFLOW.read_text(encoding="utf-8")
    common_gate = COMMON_OCR_GATE.read_text(encoding="utf-8")
    reduction_gate = REDUCTION_GATE.read_text(encoding="utf-8")

    for fragment in (
        "TryGetProductionRuntimeState(",
        "engine.UsesGpuRecognizerCtcReduction",
        "engine.RetainsFullRecognizerOutputWorker",
        "gpuCtcReduction &&",
        "!fullOutputWorkerRetained",
        'recognizer_gpu_ctc_reduction=',
        'full_output_worker_retained=',
    ):
        require(ocr, fragment, "Quest OCR smoke")

    for fragment in (
        "public bool UsesGpuRecognizerCtcReduction => recognizer.UsesGpuCtcReduction",
        "public bool RetainsFullRecognizerOutputWorker => recognizer.RetainsFullOutputWorker",
        "recognizer.ExecuteReduced(",
        "PaddleOcrRuntimeContract.ValidateRecognizerReduced(",
    ):
        require(engine, fragment, "live PP-OCR engine")

    for fragment in (
        "public bool UsesGpuCtcReduction => true",
        "public bool RetainsFullOutputWorker => false",
        "private readonly Worker reducedOutputWorker",
        "Functional.ArgMax(probabilities, dim: -1, keepdim: false)",
        "Functional.ReduceMax(probabilities, dim: -1, keepdim: false)",
        "using (var parityWorker = new Worker(ModelLoader.Load(modelAsset), backendType))",
    ):
        require(recognizer, fragment, "recognizer runtime")
    forbid(recognizer, "private readonly Worker fullOutputWorker", "recognizer runtime")

    for fragment in (
        'RECOGNIZER_GPU_REDUCTION_MARKER = "recognizer_gpu_ctc_reduction=true full_output_worker_retained=false"',
        '"recognizer_gpu_reduction_observed": RECOGNIZER_GPU_REDUCTION_MARKER in logcat',
        'and last_readiness["recognizer_gpu_reduction_observed"]',
        '"recognizer_gpu_reduction_observed",',
        '"recognizer_input_preprocess": "GPUShader+TextureConverter"',
        '"recognizer_input_layout": "NCHW/BGR/TopLeft"',
        '"recognizer_input_cpu_image_readback": False',
        '"recognizer_ctc_reduction": "GPUArgMax+ReduceMax"',
        '"recognizer_full_probability_matrix_cpu_readback": False',
        '"recognizer_full_output_worker_retained": False',
        '"recognizer_cpu_values_per_timestep": 2',
        '"raw_command_stderr_serialized": False',
        '"raw_command_arguments_serialized_on_failure": False',
        'raise SmokeError(f"external command failed with exit code {completed.returncode}")',
    ):
        require(runner, fragment, "Quest device runner")
    for forbidden in (
        "completed.stderr.strip()",
        '" ".join(args)',
    ):
        forbid(runner, forbidden, "Quest device runner failure evidence")

    for fragment in (
        "verify-local-ocr-inference.sh",
        "python tools/run_quest_read_mode_smoke.py",
        'assert data["readiness"]["ocr_smoke_passed"] is True',
        'assert data["readiness"]["read_mode_smoke_passed"] is True',
    ):
        require(workflow, fragment, "Quest Read Mode workflow")

    require(
        common_gate,
        'bash "$ROOT/tools/unity/verify-recognizer-gpu-reduction.sh"',
        "shared OCR host gate",
    )
    for fragment in (
        "PhraseLayerPaddleOcrRecognizerGpuReductionProbe.RunBatch",
        "full-matrix versus GPU ArgMax/ReduceMax CTC reduction parity",
    ):
        require(reduction_gate, fragment, "real-Unity recognizer reduction gate")

    return {
        "status": "pass",
        "quest_ocr_pass_requires_gpu_ctc_reduction": True,
        "quest_ocr_pass_rejects_retained_full_worker": True,
        "device_runner_requires_runtime_reduction_marker": True,
        "device_evidence_full_probability_readback_claim": False,
        "device_evidence_cpu_values_per_timestep": 2,
        "raw_command_stderr_serialized": False,
        "raw_command_arguments_serialized_on_failure": False,
        "pre_device_real_unity_reduction_parity_required": True,
        "quest_execution_still_required": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
