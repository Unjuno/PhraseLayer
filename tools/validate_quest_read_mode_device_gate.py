#!/usr/bin/env python3
"""Static anti-false-positive contract for the self-hosted Quest 3 Read Mode gate."""

from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / ".github/workflows/quest3-read-mode-smoke.yml"
RUNNER = ROOT / "tools/run_quest_read_mode_smoke.py"
BUILD_SH = ROOT / "tools/unity/build-android-read-mode-fixture.sh"
OCR_INFERENCE_SH = ROOT / "tools/unity/verify-local-ocr-inference.sh"
DETECTOR_GPU_PREPROCESS_SH = ROOT / "tools/unity/verify-ppocr-gpu-preprocess.sh"
RECOGNIZER_GPU_PREPROCESS_SH = ROOT / "tools/unity/verify-recognizer-gpu-preprocess.sh"
RECOGNIZER_GPU_REDUCTION_SH = ROOT / "tools/unity/verify-recognizer-gpu-reduction.sh"
BUILD_CS = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerReadModeFixtureAndroidBuild.cs"
OCR_SMOKE_CS = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/QuestOcrSmokeTestBehaviour.cs"
ENGINE_CS = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/UnityPaddleOcrEngine.cs"
RECOGNIZER_CS = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/UnityPaddleOcrRecognizerRuntime.cs"
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
    runner = RUNNER.read_text(encoding="utf-8")
    build_sh = BUILD_SH.read_text(encoding="utf-8")
    ocr_inference = OCR_INFERENCE_SH.read_text(encoding="utf-8")
    detector_preprocess = DETECTOR_GPU_PREPROCESS_SH.read_text(encoding="utf-8")
    recognizer_preprocess = RECOGNIZER_GPU_PREPROCESS_SH.read_text(encoding="utf-8")
    recognizer_reduction = RECOGNIZER_GPU_REDUCTION_SH.read_text(encoding="utf-8")
    build_cs = BUILD_CS.read_text(encoding="utf-8")
    ocr_smoke_cs = OCR_SMOKE_CS.read_text(encoding="utf-8")
    engine_cs = ENGINE_CS.read_text(encoding="utf-8")
    recognizer_cs = RECOGNIZER_CS.read_text(encoding="utf-8")
    apk_inspector = APK_INSPECTOR.read_text(encoding="utf-8")

    for fragment in (
        "workflow_dispatch:",
        "runs-on: [self-hosted, unity, unity-6000-0-66f2, quest3, adb]",
        'default: "Quest 3"',
        "python tools/stage_models.py --purpose-prefix ocr- --include-support",
        "python tools/prepare_unity_ocr_assets.py",
        "verify-local-ocr-inference.sh",
        "verify-ppocr-gpu-preprocess.sh",
        "verify-recognizer-gpu-preprocess.sh",
        "build-android-read-mode-fixture.sh",
        "python tools/run_quest_read_mode_smoke.py",
        'assert data["readiness"]["ocr_smoke_passed"] is True',
        'assert data["readiness"]["read_mode_smoke_passed"] is True',
        'assert data["readiness"]["captured_pose_projection_observed"] is True',
        'assert data["permissions"]["android.permission.CAMERA"]["declared"] is True',
        'assert data["permissions"]["horizonos.permission.HEADSET_CAMERA"]["declared"] is True',
        'assert data["camera_pixel_pose_sync_verified"] is False',
        "inspect_android_apk_structure.py",
        "Remove local Read Mode APK before artifact phase",
        'rm -f "$RUNNER_TEMP/PhraseLayerReadModeFixture.apk"',
        "Upload safe Quest 3 Read Mode evidence",
        "phraselayer-quest3-read-mode-evidence",
    ):
        require(workflow, fragment, "Quest Read Mode workflow")

    upload_section = workflow.split("- name: Upload safe Quest 3 Read Mode evidence", 1)[1]
    for forbidden in (
        "PhraseLayerReadModeFixture.apk",
        "detector.onnx",
        "recognizer.onnx",
        "quest-read-mode-logcat.txt",
        "quest-read-mode-smoke/**",
    ):
        forbid(upload_section, forbidden, "Quest Read Mode artifact upload section")

    for fragment in (
        "PhraseLayerLocalOcrAssets.RunLocalInferenceProbeBatch",
        'bash "$ROOT/tools/unity/verify-recognizer-gpu-reduction.sh"',
        "synthetic GPU inference plus recognizer GPU CTC reduction parity",
    ):
        require(ocr_inference, fragment, "shared PP-OCR real Unity gate")

    for shell, label, method in (
        (detector_preprocess, "detector preprocess gate", "PhraseLayerPaddleOcrGpuPreprocessProbe.RunBatch"),
        (recognizer_preprocess, "recognizer preprocess gate", "PhraseLayerPaddleOcrRecognizerGpuPreprocessProbe.RunBatch"),
        (recognizer_reduction, "recognizer reduction gate", "PhraseLayerPaddleOcrRecognizerGpuReductionProbe.RunBatch"),
    ):
        require(shell, "UNITY_EDITOR", label)
        require(shell, method, label)
        if has_nographics_argument(shell):
            raise GateError(f"{label} must run with a real graphics device")

    for fragment in (
        "full-matrix versus GPU ArgMax/ReduceMax CTC reduction parity",
        "recognizer.onnx",
        "ppocr_keys.txt",
    ):
        require(recognizer_reduction, fragment, "recognizer reduction gate")

    for fragment in (
        "public bool UsesGpuCtcReduction => true",
        "public bool RetainsFullOutputWorker => false",
        "private readonly Worker reducedOutputWorker",
        "Functional.ArgMax(probabilities, dim: -1, keepdim: false)",
        "Functional.ReduceMax(probabilities, dim: -1, keepdim: false)",
        "using (var parityWorker = new Worker(ModelLoader.Load(modelAsset), backendType))",
    ):
        require(recognizer_cs, fragment, "recognizer runtime")
    forbid(recognizer_cs, "private readonly Worker fullOutputWorker", "recognizer runtime")

    for fragment in (
        "public bool UsesGpuRecognizerCtcReduction => recognizer.UsesGpuCtcReduction",
        "public bool RetainsFullRecognizerOutputWorker => recognizer.RetainsFullOutputWorker",
        "recognizer.ExecuteReduced(",
        "PaddleOcrRuntimeContract.ValidateRecognizerReduced(",
    ):
        require(engine_cs, fragment, "live PP-OCR engine")

    for fragment in (
        "TryGetProductionRuntimeState(",
        "engine.UsesGpuRecognizerCtcReduction",
        "engine.RetainsFullRecognizerOutputWorker",
        "gpuCtcReduction &&",
        "!fullOutputWorkerRetained",
        'recognizer_gpu_ctc_reduction=',
        'full_output_worker_retained=',
    ):
        require(ocr_smoke_cs, fragment, "Quest OCR smoke")

    for fragment in (
        'DEFAULT_PACKAGE = "com.unjuno.phraselayer.readmodefixture"',
        'DEFAULT_EXPECTED_DEVICE_MODEL = "Quest 3"',
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
        '"camera_timestamp_source": "MetaPassthroughCameraAccess.Timestamp"',
        '"camera_pose_source": "MetaPassthroughCameraAccess.GetCameraPose"',
        '"captured_pose_projection_required": True',
        '"camera_pixel_pose_sync_verified": False',
        '"raw_process_logcat_written_to_disk": False',
        '"raw_process_logcat_uploaded": False',
        '"raw_command_stderr_serialized": False',
        '"raw_command_arguments_serialized_on_failure": False',
        "pattern.fullmatch(candidate)",
        "diagnostics_path.write_text(sanitize_logcat_diagnostics(logcat)",
    ):
        require(runner, fragment, "Quest Read Mode device runner")

    for forbidden in (
        '"adb_serial": serial',
        "completed.stderr.strip()",
        '" ".join(args)',
        "quest-read-mode-logcat.txt",
        "log_path.write_text(logcat",
    ):
        forbid(runner, forbidden, "Quest Read Mode evidence privacy boundary")

    for fragment in (
        "zipfile.is_zipfile",
        'abis != ["arm64-v8a"]',
        '"lib/arm64-v8a/libil2cpp.so"',
        'name.startswith("assets/bin/Data/")',
        '"runtime_execution_performed": False',
    ):
        require(apk_inspector, fragment, "Android APK structure inspector")

    for fragment in (
        "PhraseLayerQuestProjectSetup.ApplyAndroidRequiredFixesBatch",
        "PhraseLayerReadModeFixtureAndroidBuild.BuildBatch",
        "PHRASELAYER_READ_MODE_FIXTURE_APK_PATH",
    ):
        require(build_sh, fragment, "Read Mode Android build shell")

    for fragment in (
        'DefaultApplicationIdentifier = "com.unjuno.phraselayer.readmodefixture"',
        'Application.dataPath',
        'PlayerSettings.SetScriptingBackend(namedTarget, ScriptingImplementation.IL2CPP)',
        'PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64',
        '\\"translation_runtime\\": \\"DemoDictionaryFixture\\"',
        '\\"product_translation_gate\\": false',
        '\\"camera_pixel_pose_sync_verified\\": false',
        '\\"quest_read_mode_smoke_autorun\\": true',
    ):
        require(build_cs, fragment, "Read Mode fixture Android builder")

    return {
        "status": "pass",
        "self_hosted_quest3_runner_required": True,
        "actual_device_model_verified": True,
        "pinned_ocr_staged": True,
        "real_unity_detector_preprocess_parity_required": True,
        "real_unity_recognizer_preprocess_parity_required": True,
        "real_unity_recognizer_reduction_parity_required": True,
        "quest_pass_requires_gpu_ctc_reduction": True,
        "production_full_recognizer_worker_allowed": False,
        "recognizer_full_probability_matrix_cpu_readback": False,
        "recognizer_cpu_values_per_timestep": 2,
        "raw_adb_serial_uploaded": False,
        "raw_process_logcat_written_to_disk": False,
        "raw_process_logcat_uploaded": False,
        "raw_command_stderr_serialized": False,
        "raw_command_arguments_serialized_on_failure": False,
        "allowlisted_diagnostics_required": True,
        "captured_camera_pose_required": True,
        "mruk_live_depth_surface_required": True,
        "android_arm64_il2cpp_required": True,
        "apk_structure_verification_required": True,
        "apk_artifact_upload_allowed": False,
        "pixel_pose_sync_false_claim_prevented": True,
        "fixture_translation_not_product_gate": True,
        "real_quest_execution_still_required": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
