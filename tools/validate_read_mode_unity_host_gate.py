#!/usr/bin/env python3
"""Static contract for the Read Mode host-only Unity gate that must precede Quest execution."""

from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / ".github/workflows/read-mode-unity-host-gate.yml"
OCR_INFERENCE_SH = ROOT / "tools/unity/verify-local-ocr-inference.sh"
DETECTOR_GPU_PREPROCESS_SH = ROOT / "tools/unity/verify-ppocr-gpu-preprocess.sh"
RECOGNIZER_GPU_PREPROCESS_SH = ROOT / "tools/unity/verify-recognizer-gpu-preprocess.sh"
RECOGNIZER_GPU_REDUCTION_SH = ROOT / "tools/unity/verify-recognizer-gpu-reduction.sh"
BUILD_SH = ROOT / "tools/unity/build-android-read-mode-fixture.sh"
APK_INSPECTOR = ROOT / "tools/inspect_android_apk_structure.py"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def forbid(text: str, fragment: str, label: str) -> None:
    if fragment in text:
        raise GateError(f"{label} contains forbidden marker: {fragment}")


def has_nographics_argument(shell: str) -> bool:
    return re.search(r"(?m)^\s*-nographics(?:\s|\\|$)", shell) is not None


def validate() -> dict[str, object]:
    workflow = WORKFLOW.read_text(encoding="utf-8")
    ocr_inference = OCR_INFERENCE_SH.read_text(encoding="utf-8")
    detector_gpu_preprocess = DETECTOR_GPU_PREPROCESS_SH.read_text(encoding="utf-8")
    recognizer_gpu_preprocess = RECOGNIZER_GPU_PREPROCESS_SH.read_text(encoding="utf-8")
    recognizer_gpu_reduction = RECOGNIZER_GPU_REDUCTION_SH.read_text(encoding="utf-8")
    build = BUILD_SH.read_text(encoding="utf-8")
    apk_inspector = APK_INSPECTOR.read_text(encoding="utf-8")

    for fragment in (
        "workflow_dispatch:",
        "runs-on: [self-hosted, unity, unity-6000-0-66f2]",
        "japanese_font_source:",
        "python tools/stage_models.py --purpose-prefix ocr- --include-support",
        "python tools/prepare_unity_ocr_assets.py",
        "verify-local-ocr-inference.sh",
        "Require real Unity PP-OCR detector GPU preprocess parity",
        "verify-ppocr-gpu-preprocess.sh",
        "Require real Unity PP-OCR recognizer GPU preprocess parity",
        "verify-recognizer-gpu-preprocess.sh",
        "build-android-read-mode-fixture.sh",
        'build["architecture"] == "ARM64"',
        'build["scripting_backend"] == "IL2CPP"',
        'build["deterministic_single_scene_build"] is True',
        'build["project_paths_anchored_to_application_data_path"] is True',
        'visual["font_staged_bytes_verified"] is True',
        'visual["mask_shader_reasserted"] is True',
        "inspect_android_apk_structure.py",
        "read-mode-apk-fingerprint.json",
        "read-mode-apk-structure.json",
        '"ocr_model_redistribution_review":"pending"',
        '"apk_uploaded":False',
        '"apk_removed_before_artifact_upload":True',
        '"apk_structure_verified":True',
        '"real_unity_pinned_ocr_inference_passed":True',
        '"real_unity_detector_gpu_preprocess_parity_passed":True',
        '"real_unity_recognizer_gpu_preprocess_parity_passed":True',
        '"android_arm64_il2cpp_fixture_built":True',
        '"quest_device_execution_performed":False',
        '"product_translation_gate":False',
        'apk.unlink()',
        "Remove local Read Mode APK before artifact phase",
        'rm -f "$RUNNER_TEMP/PhraseLayerReadModeFixture.apk"',
        "phraselayer-read-mode-unity-host-evidence",
        "if: always()",
    ):
        require(workflow.replace(" ", ""), fragment.replace(" ", ""), "Read Mode Unity host workflow")

    for forbidden in (
        "runs-on: [self-hosted, unity, unity-6000-0-66f2, quest3",
        "run_quest_read_mode_smoke.py",
        "adb devices",
        "--serial",
    ):
        forbid(workflow, forbidden, "Read Mode Unity host workflow")

    upload_section = workflow.split("- name: Upload safe pre-device Read Mode Unity evidence", 1)[1]
    for forbidden in (
        "PhraseLayerReadModeFixture.apk",
        "detector.onnx",
        "recognizer.onnx",
    ):
        forbid(upload_section, forbidden, "Read Mode host artifact upload section")

    for fragment in (
        "zipfile.is_zipfile",
        'abis != ["arm64-v8a"]',
        '"lib/arm64-v8a/libil2cpp.so"',
        'name.startswith("assets/bin/Data/")',
        '"runtime_execution_performed": False',
    ):
        require(apk_inspector, fragment, "Android APK structure inspector")

    for fragment in (
        "Intentionally no -nographics",
        "PhraseLayerLocalOcrAssets.RunLocalInferenceProbeBatch",
        'bash "$ROOT/tools/unity/verify-recognizer-gpu-reduction.sh"',
        "synthetic GPU inference plus recognizer GPU CTC reduction parity",
    ):
        require(ocr_inference, fragment, "real Unity OCR inference shell")

    for fragment in (
        "Intentionally no -nographics",
        "PhraseLayerPaddleOcrGpuPreprocessProbe.RunBatch",
        "real Unity PP-OCR GPU texture -> tensor -> normalization parity probe",
    ):
        require(detector_gpu_preprocess, fragment, "detector GPU preprocess shell")

    for fragment in (
        "UNITY_EDITOR",
        "PhraseLayerPaddleOcrRecognizerGpuPreprocessProbe.RunBatch",
        "real Unity PP-OCR recognizer GPU preprocessing parity",
    ):
        require(recognizer_gpu_preprocess, fragment, "recognizer GPU preprocess shell")
    if has_nographics_argument(recognizer_gpu_preprocess):
        raise GateError("recognizer GPU preprocess shell must require a real graphics device")

    for fragment in (
        "UNITY_EDITOR",
        "PhraseLayerPaddleOcrRecognizerGpuReductionProbe.RunBatch",
        "full-matrix versus GPU ArgMax/ReduceMax CTC reduction parity",
    ):
        require(recognizer_gpu_reduction, fragment, "recognizer GPU reduction shell")
    if has_nographics_argument(recognizer_gpu_reduction):
        raise GateError("recognizer GPU reduction shell must require a real graphics device")

    for fragment in (
        "PhraseLayerQuestProjectSetup.ApplyAndroidRequiredFixesBatch",
        "PhraseLayerReadModeFixtureAndroidBuild.BuildBatch",
        "PHRASELAYER_READ_MODE_FIXTURE_APK_PATH",
    ):
        require(build, fragment, "Read Mode Android build shell")

    return {
        "status": "pass",
        "quest_or_adb_dependency": False,
        "real_unity_pinned_ocr_inference_required": True,
        "real_unity_detector_gpu_preprocess_parity_required": True,
        "real_unity_recognizer_gpu_preprocess_parity_required": True,
        "real_unity_recognizer_gpu_reduction_parity_required": True,
        "production_full_recognizer_worker_allowed": False,
        "android_arm64_il2cpp_build_required": True,
        "reviewed_font_and_mask_evidence_required": True,
        "deterministic_single_scene_required": True,
        "apk_structure_verification_required": True,
        "ocr_redistribution_review_pending": True,
        "apk_artifact_upload_allowed": False,
        "host_artifact_manifest_required": True,
        "quest_execution_deferred": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
