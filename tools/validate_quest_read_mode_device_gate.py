#!/usr/bin/env python3
"""Static anti-false-positive contract for the self-hosted Quest 3 Read Mode gate."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / ".github/workflows/quest3-read-mode-smoke.yml"
RUNNER = ROOT / "tools/run_quest_read_mode_smoke.py"
BUILD_SH = ROOT / "tools/unity/build-android-read-mode-fixture.sh"
OCR_INFERENCE_SH = ROOT / "tools/unity/verify-local-ocr-inference.sh"
GPU_PREPROCESS_SH = ROOT / "tools/unity/verify-ppocr-gpu-preprocess.sh"
GPU_PREPROCESS_CS = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerPaddleOcrGpuPreprocessProbe.cs"
BUILD_CS = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerReadModeFixtureAndroidBuild.cs"
APK_INSPECTOR = ROOT / "tools/inspect_android_apk_structure.py"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def forbid(text: str, fragment: str, label: str) -> None:
    if fragment in text:
        raise GateError(f"{label} contains forbidden marker: {fragment}")


def validate() -> dict[str, object]:
    workflow = WORKFLOW.read_text(encoding="utf-8")
    runner = RUNNER.read_text(encoding="utf-8")
    build_sh = BUILD_SH.read_text(encoding="utf-8")
    ocr_inference_sh = OCR_INFERENCE_SH.read_text(encoding="utf-8")
    gpu_preprocess_sh = GPU_PREPROCESS_SH.read_text(encoding="utf-8")
    gpu_preprocess_cs = GPU_PREPROCESS_CS.read_text(encoding="utf-8")
    build_cs = BUILD_CS.read_text(encoding="utf-8")
    apk_inspector = APK_INSPECTOR.read_text(encoding="utf-8")

    for fragment in (
        "workflow_dispatch:",
        "runs-on: [self-hosted, unity, unity-6000-0-66f2, quest3, adb]",
        'default: "Quest 3"',
        "python tools/stage_models.py --purpose-prefix ocr- --include-support",
        "python tools/prepare_unity_ocr_assets.py",
        "Require real Unity pinned PP-OCR synthetic inference",
        "verify-local-ocr-inference.sh",
        "python tools/validate_ppocr_gpu_preprocess_gate.py",
        "Require real Unity PP-OCR GPU preprocess parity",
        "verify-ppocr-gpu-preprocess.sh",
        "PHRASELAYER_JAPANESE_FONT_SOURCE:",
        "build-android-read-mode-fixture.sh",
        'assert data["surface_runtime"] == "MRUKEnvironmentRaycast"',
        'assert data["translation_runtime"] == "DemoDictionaryFixture"',
        'assert data["product_translation_gate"] is False',
        'assert data["deterministic_single_scene_build"] is True',
        'assert data["project_paths_anchored_to_application_data_path"] is True',
        'assert visual_data["font_staged_bytes_verified"] is True',
        'assert visual_data["mask_shader_reasserted"] is True',
        "inspect_android_apk_structure.py",
        "read-mode-apk-fingerprint.json",
        "read-mode-apk-structure.json",
        '"ocr_model_redistribution_review": "pending"',
        '"uploaded": False',
        "python tools/run_quest_read_mode_smoke.py",
        'assert data["readiness"]["ocr_smoke_passed"] is True',
        'assert data["readiness"]["read_mode_smoke_passed"] is True',
        'assert data["readiness"]["captured_pose_projection_observed"] is True',
        'assert data["permissions"]["android.permission.CAMERA"]["declared"] is True',
        'assert data["permissions"]["horizonos.permission.HEADSET_CAMERA"]["declared"] is True',
        'assert data["camera_timestamp_source"] == "MetaPassthroughCameraAccess.Timestamp"',
        'assert data["camera_pose_source"] == "MetaPassthroughCameraAccess.GetCameraPose"',
        'assert data["captured_pose_projection_required"] is True',
        'assert data["camera_timestamp_pose_binding_implemented"] is True',
        'assert data["camera_pixel_pose_sync_verified"] is False',
        'assert data["apk"]["sha256"] == fingerprint["sha256"]',
        "Remove local Read Mode APK before artifact phase",
        'rm -f "$RUNNER_TEMP/PhraseLayerReadModeFixture.apk"',
        "Upload safe Quest 3 Read Mode evidence",
        "if: always()",
        "phraselayer-quest3-read-mode-evidence",
    ):
        require(workflow, fragment, "Quest Read Mode workflow")

    upload_section = workflow.split("- name: Upload safe Quest 3 Read Mode evidence", 1)[1]
    for forbidden_marker in (
        "PhraseLayerReadModeFixture.apk",
        "detector.onnx",
        "recognizer.onnx",
    ):
        forbid(upload_section, forbidden_marker, "Quest Read Mode artifact upload section")

    for fragment in (
        "zipfile.is_zipfile",
        'abis != ["arm64-v8a"]',
        '"lib/arm64-v8a/libil2cpp.so"',
        'name.startswith("assets/bin/Data/")',
        '"runtime_execution_performed": False',
    ):
        require(apk_inspector, fragment, "Android APK structure inspector")

    for fragment in (
        'DEFAULT_PACKAGE = "com.unjuno.phraselayer.readmodefixture"',
        'DEFAULT_EXPECTED_DEVICE_MODEL = "Quest 3"',
        'CAMERA_PERMISSION = "android.permission.CAMERA"',
        'HEADSET_CAMERA_PERMISSION = "horizonos.permission.HEADSET_CAMERA"',
        'OCR_PASS_MARKER = "PhraseLayer Quest OCR smoke test PASS"',
        'READ_MODE_PASS_MARKER = "PhraseLayer Quest Read Mode smoke test PASS"',
        'SURFACE_RUNTIME_MARKER = "surface_runtime=MRUKEnvironmentRaycast"',
        'CAPTURED_POSE_MARKER = "captured_pose_projection=true"',
        'FATAL_MARKER = "FATAL EXCEPTION"',
        "require_device_model(actual_device_model, args.expected_device_model)",
        "permission_declared(package_dump, CAMERA_PERMISSION)",
        "permission_declared(package_dump, HEADSET_CAMERA_PERMISSION)",
        "if not camera_declared:",
        "if not headset_camera_declared:",
        '"logcat", "-c"',
        '"install", "-r", "-g"',
        '"adb_serial_sha256_12": serial_fingerprint(serial)',
        'status="fail"',
        'evidence_path.write_text',
        '"detector_input_preprocess": "GPUTextureConverter+FunctionalNormalization"',
        '"detector_input_layout": "NCHW/BGR/TopLeft"',
        '"detector_input_cpu_image_readback": False',
        '"surface_runtime": "MRUKEnvironmentRaycast"',
        '"translation_runtime": "DemoDictionaryFixture"',
        '"product_translation_gate": False',
        '"camera_timestamp_source": "MetaPassthroughCameraAccess.Timestamp"',
        '"camera_pose_source": "MetaPassthroughCameraAccess.GetCameraPose"',
        '"captured_pose_projection_required": True',
        '"camera_timestamp_pose_binding_implemented": True',
        '"camera_pixel_pose_sync_verified": False',
        '"scope": (',
    ):
        require(runner, fragment, "Quest Read Mode device runner")
    if '"adb_serial": serial' in runner:
        raise GateError("Quest evidence must not upload the raw adb serial")
    forbid(runner, "Graphics.Blit/readback preprocessing path", "Quest Read Mode device runner")

    for fragment in (
        "UNITY_EDITOR must point to the Unity 6000.0.66f2 Editor executable.",
        "PHRASELAYER_JAPANESE_FONT_SOURCE",
        "PHRASELAYER_READ_MODE_FIXTURE_APK_PATH",
        "PhraseLayerReadModeFixtureAndroidBuild.BuildBatch",
        "PhraseLayer.read-mode-fixture-build-evidence.json",
    ):
        require(build_sh, fragment, "Read Mode Android build shell")

    for fragment in (
        "UNITY_EDITOR must point to the Unity 6000.0.66f2 Editor executable.",
        "Required staged PP-OCR asset is missing or empty",
        "Intentionally no -nographics",
        "PhraseLayerLocalOcrAssets.RunLocalInferenceProbeBatch",
        "real Unity pinned PP-OCR detector+recognizer synthetic GPU inference gate",
    ):
        require(ocr_inference_sh, fragment, "PP-OCR real Unity inference shell")

    for fragment in (
        "UNITY_EDITOR must point to the Unity 6000.0.66f2 Editor executable.",
        "Intentionally no -nographics",
        "PhraseLayerPaddleOcrGpuPreprocessProbe.RunBatch",
        "real Unity PP-OCR GPU texture -> tensor -> normalization parity probe",
    ):
        require(gpu_preprocess_sh, fragment, "PP-OCR GPU preprocess Unity shell")

    for fragment in (
        "ProbeSize = PaddleOcrV6TinyDetectionPreprocess.DefaultLimitSideLength",
        "CreateReviewedTextureTransform(flipReadbackRows: true)",
        "ApplyReviewedNormalization(normalizationInput)",
        "PaddleOcrV6TinyDetectionPreprocess.NormalizeChannel(pixel.b, 0)",
        "PaddleOcrV6TinyDetectionPreprocess.NormalizeChannel(pixel.g, 1)",
        "PaddleOcrV6TinyDetectionPreprocess.NormalizeChannel(pixel.r, 2)",
        "PhraseLayer PP-OCR GPU preprocess parity PASS",
        "public static void RunBatch()",
    ):
        require(gpu_preprocess_cs, fragment, "PP-OCR GPU preprocess Unity probe")

    for fragment in (
        'DefaultApplicationIdentifier = "com.unjuno.phraselayer.readmodefixture"',
        'var root = ProjectRoot()',
        'Application.dataPath',
        'enabledScenes.Length != 1',
        'new[] { PhraseLayerEditorSetup.DemoScenePath }',
        '\\"ocr_runtime\\": \\"PaddleOCR\\"',
        '\\"surface_runtime\\": \\"MRUKEnvironmentRaycast\\"',
        '\\"translation_runtime\\": \\"DemoDictionaryFixture\\"',
        '\\"product_translation_gate\\": false',
        '\\"camera_timestamp_source\\": \\"MetaPassthroughCameraAccess.Timestamp\\"',
        '\\"camera_pose_source\\": \\"MetaPassthroughCameraAccess.GetCameraPose\\"',
        '\\"captured_pose_projection_required\\": true',
        '\\"camera_timestamp_pose_binding_implemented\\": true',
        '\\"camera_pixel_pose_sync_verified\\": false',
        '\\"quest_read_mode_smoke_autorun\\": true',
        '\\"deterministic_single_scene_build\\": true',
        '\\"project_paths_anchored_to_application_data_path\\": true',
        'PlayerSettings.SetScriptingBackend(namedTarget, ScriptingImplementation.IL2CPP)',
        'PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64',
    ):
        require(build_cs, fragment, "Read Mode fixture Android builder")

    return {
        "status": "pass",
        "self_hosted_quest3_runner_required": True,
        "actual_device_model_verified": True,
        "raw_adb_serial_uploaded": False,
        "failure_json_required": True,
        "installed_camera_permissions_required": True,
        "pinned_ocr_staged": True,
        "real_unity_pinned_ocr_inference_required": True,
        "real_unity_gpu_preprocess_parity_required": True,
        "detector_cpu_image_readback_forbidden": True,
        "captured_camera_pose_required": True,
        "mruk_live_depth_surface_required": True,
        "reviewed_external_japanese_font_required": True,
        "deterministic_single_scene_build_required": True,
        "project_paths_anchored_to_application_data_path": True,
        "android_arm64_il2cpp_required": True,
        "apk_structure_verification_required": True,
        "ocr_redistribution_review_pending": True,
        "apk_artifact_upload_allowed": False,
        "ocr_and_read_mode_pass_markers_required": True,
        "fatal_exception_rejected": True,
        "timestamp_pose_binding_implemented": True,
        "pixel_pose_sync_false_claim_prevented": True,
        "fixture_translation_not_product_gate": True,
        "evidence_uploaded_on_failure": True,
        "real_quest_execution_still_required": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
