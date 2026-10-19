#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "tools" / "run_quest_read_mode_smoke.py"
spec = importlib.util.spec_from_file_location("run_quest_read_mode_smoke", MODULE_PATH)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)


def expect_raises(fn, exc_type) -> None:
    try:
        fn()
    except exc_type:
        return
    raise AssertionError(f"expected {exc_type.__name__}")


def main() -> None:
    devices = module.parse_adb_devices(
        "List of devices attached\n"
        "1WMHH000000000 device product:eureka model:Quest_3 device:eureka transport_id:1\n"
        "emulator-5554 offline transport_id:2\n"
    )
    assert devices == ["1WMHH000000000"]
    assert module.choose_serial(devices, None) == "1WMHH000000000"
    assert module.choose_serial(devices, "1WMHH000000000") == "1WMHH000000000"
    expect_raises(lambda: module.choose_serial([], None), module.SmokeError)
    expect_raises(lambda: module.choose_serial(["a", "b"], None), module.SmokeError)
    expect_raises(lambda: module.choose_serial(["a"], "b"), module.SmokeError)

    module.require_device_model("Quest 3", "Quest 3")
    module.require_device_model("Quest_3", "Quest 3")
    module.require_device_model(" quest-3 ", "Quest 3")
    expect_raises(lambda: module.require_device_model("Pixel 10", "Quest 3"), module.SmokeError)

    permissions = (
        "requested permissions:\n"
        "  android.permission.CAMERA\n"
        "  horizonos.permission.HEADSET_CAMERA\n"
        "runtime permissions:\n"
        "  android.permission.CAMERA: granted=true\n"
        "  horizonos.permission.HEADSET_CAMERA: granted=false\n"
    )
    assert module.permission_declared(permissions, module.CAMERA_PERMISSION) is True
    assert module.permission_declared(permissions, module.HEADSET_CAMERA_PERMISSION) is True
    assert module.permission_declared(permissions, "android.permission.RECORD_AUDIO") is False
    assert module.permission_granted(permissions, module.CAMERA_PERMISSION) is True
    assert module.permission_granted(permissions, module.HEADSET_CAMERA_PERMISSION) is False
    assert module.permission_granted(permissions, "android.permission.RECORD_AUDIO") is None

    fingerprint = module.serial_fingerprint("1WMHH000000000")
    assert fingerprint is not None and len(fingerprint) == 12
    assert fingerprint == module.serial_fingerprint("1WMHH000000000")
    assert fingerprint != "1WMHH000000000"
    assert module.serial_fingerprint(None) is None

    redacted_failure = module.redact_failure_message(
        module.SmokeError("device 1WMHH000000000 failed"),
        "1WMHH000000000",
    )
    assert "1WMHH000000000" not in redacted_failure
    assert "<redacted-adb-serial>" in redacted_failure

    empty = module.readiness_from_logcat("")
    assert empty == {
        "ocr_smoke_passed": False,
        "read_mode_smoke_passed": False,
        "mruk_environment_raycast_observed": False,
        "captured_pose_projection_observed": False,
        "recognizer_gpu_reduction_observed": False,
        "read_mode_timeout": False,
        "read_mode_exception": False,
        "fatal_exception": False,
    }

    passed = module.readiness_from_logcat(
        "PhraseLayer Quest OCR smoke test PASS\n"
        "recognizer_gpu_ctc_reduction=true full_output_worker_retained=false\n"
        "PhraseLayer Quest Read Mode smoke test PASS\n"
        "camera_timestamp_source=MetaPassthroughCameraAccess.Timestamp captured_pose_projection=true captured_pose_rays=5\n"
        "surface_runtime=MRUKEnvironmentRaycast environment_abi_validated=true\n"
    )
    assert passed["ocr_smoke_passed"] is True
    assert passed["read_mode_smoke_passed"] is True
    assert passed["mruk_environment_raycast_observed"] is True
    assert passed["captured_pose_projection_observed"] is True
    assert passed["recognizer_gpu_reduction_observed"] is True
    assert passed["fatal_exception"] is False

    incomplete = module.readiness_from_logcat(
        "PhraseLayer Quest OCR smoke test PASS\n"
        "PhraseLayer Quest Read Mode smoke test PASS\n"
        "surface_runtime=MRUKEnvironmentRaycast environment_abi_validated=true\n"
    )
    assert incomplete["mruk_environment_raycast_observed"] is True
    assert incomplete["captured_pose_projection_observed"] is False
    assert incomplete["recognizer_gpu_reduction_observed"] is False

    failed = module.readiness_from_logcat(
        "PhraseLayer Quest Read Mode smoke test FAIL_TIMEOUT\nFATAL EXCEPTION\n"
    )
    assert failed["read_mode_timeout"] is True
    assert failed["fatal_exception"] is True

    raw_log = (
        "09-02 10:00:00.000 123 123 I Unity : PhraseLayer Quest OCR smoke test PASS\n"
        "09-02 10:00:00.001 123 123 I Unity : regions=1 overall_confidence=0.990000 text_length=14\n"
        "09-02 10:00:00.002 123 123 I Unity : recognizer_gpu_ctc_reduction=true full_output_worker_retained=false\n"
        "09-02 10:00:00.003 123 123 I Unity : recognized_text=PRIVATE STREET SIGN\n"
        "09-02 10:00:00.004 123 123 I Unity : display_text=秘密の表示\n"
        "09-02 10:00:00.005 123 123 I Other : arbitrary user content SECRET-123\n"
        "09-02 10:00:00.006 123 123 I Unity : camera_timestamp_source=MetaPassthroughCameraAccess.Timestamp captured_pose_projection=true captured_pose_rays=5 stable_capture_metadata=2 unstable_capture_metadata=0 pixel_pose_sync_verified=false\n"
        "09-02 10:00:00.007 123 123 I Unity : surface_runtime=MRUKEnvironmentRaycast environment_abi_validated=true last_environment_status=Success last_normal_confidence=0.9500\n"
        "09-02 10:00:00.008 123 123 I Unity : layout_ready=1 layout_failed=0 tracks_observed=1 tracks_retained=0\n"
        "09-02 10:00:00.009 123 123 I Unity : mask_render_success=true masks_active=1 masks_eligible=1 masks_suppressed=0\n"
        "09-02 10:00:00.010 123 123 I Unity : text_render_success=true rendered_views=1 max_observed_planarity_error_m=0.002500\n"
        "09-02 10:00:00.011 123 123 I Unity : ocr_stage=PASS\n"
        "09-02 10:00:00.012 123 123 I Unity : regions=1 overall_confidence=0.990000 text_length=14 recognized_text=PREFIX-LEAK\n"
        "09-02 10:00:00.013 123 123 I Unity : recognizer_gpu_ctc_reduction=true full_output_worker_retained=false secret=SUFFIX-LEAK\n"
        "09-02 10:00:00.014 123 123 I Unity : surface_runtime=MRUKEnvironmentRaycast environment_abi_validated=true last_environment_status=Success last_normal_confidence=0.9500 secret=SUFFIX-LEAK\n"
        "09-02 10:00:00.015 123 123 E AndroidRuntime : FATAL EXCEPTION: main private-stack-text\n"
    )
    sanitized = module.sanitize_logcat_diagnostics(raw_log)
    safe_lines = sanitized.splitlines()
    assert "PhraseLayer Quest OCR smoke test PASS" in safe_lines
    assert "regions=1 overall_confidence=0.990000 text_length=14" in safe_lines
    assert "recognizer_gpu_ctc_reduction=true full_output_worker_retained=false" in safe_lines
    assert any(line.startswith("camera_timestamp_source=MetaPassthroughCameraAccess.Timestamp") for line in safe_lines)
    assert any(line.startswith("surface_runtime=MRUKEnvironmentRaycast") for line in safe_lines)
    assert "layout_ready=1 layout_failed=0 tracks_observed=1 tracks_retained=0" in safe_lines
    assert "mask_render_success=true masks_active=1 masks_eligible=1 masks_suppressed=0" in safe_lines
    assert "text_render_success=true rendered_views=1 max_observed_planarity_error_m=0.002500" in safe_lines
    assert "ocr_stage=PASS" in safe_lines
    assert "FATAL EXCEPTION" in safe_lines
    assert len(safe_lines) == 10
    assert "PRIVATE STREET SIGN" not in sanitized
    assert "秘密の表示" not in sanitized
    assert "SECRET-123" not in sanitized
    assert "PREFIX-LEAK" not in sanitized
    assert "SUFFIX-LEAK" not in sanitized
    assert "private-stack-text" not in sanitized
    assert "09-02 10:00:00" not in sanitized

    source = MODULE_PATH.read_text(encoding="utf-8")
    assert '"adb_serial": serial' not in source
    assert '"adb_serial_sha256_12": serial_fingerprint(serial)' in source
    assert 'status="fail"' in source
    assert 'evidence_path.write_text' in source
    assert '"detector_input_preprocess": "GPUTextureConverter+FunctionalNormalization"' in source
    assert '"detector_input_layout": "NCHW/BGR/TopLeft"' in source
    assert '"detector_input_cpu_image_readback": False' in source
    assert '"recognizer_input_preprocess": "GPUShader+TextureConverter"' in source
    assert '"recognizer_input_layout": "NCHW/BGR/TopLeft"' in source
    assert '"recognizer_input_cpu_image_readback": False' in source
    assert '"recognizer_ctc_reduction": "GPUArgMax+ReduceMax"' in source
    assert '"recognizer_full_probability_matrix_cpu_readback": False' in source
    assert '"recognizer_full_output_worker_retained": False' in source
    assert '"recognizer_cpu_values_per_timestep": 2' in source
    assert 'Graphics.Blit/readback preprocessing path' not in source
    assert '"camera_timestamp_source": "MetaPassthroughCameraAccess.Timestamp"' in source
    assert '"camera_pose_source": "MetaPassthroughCameraAccess.GetCameraPose"' in source
    assert '"captured_pose_projection_required": True' in source
    assert '"camera_pixel_pose_sync_verified": False' in source
    assert 'DIAGNOSTIC_START_MARKERS = (' in source
    assert 'SAFE_DIAGNOSTIC_PATTERNS = tuple(' in source
    assert 'pattern.fullmatch(candidate)' in source
    assert '"raw_process_logcat_written_to_disk": False' in source
    assert '"raw_process_logcat_uploaded": False' in source
    assert '"raw_command_stderr_serialized": False' in source
    assert '"raw_command_arguments_serialized_on_failure": False' in source
    assert '"diagnostic_lines_require_full_grammar_match": True' in source
    assert '"recognized_text_allowed_in_diagnostics": False' in source
    assert 'diagnostics_path.write_text(sanitize_logcat_diagnostics(logcat)' in source
    assert 'completed.stderr.strip()' not in source
    assert '" ".join(args)' not in source
    assert 'quest-read-mode-logcat.txt' not in source

    print(
        "PASS: Quest Read Mode device smoke parsing, permissions, redacted device/failure evidence, full-grammar diagnostics, "
        "malicious suffix rejection, detector+recognizer GPU preprocessing, GPU CTC reduction, captured camera-pose requirement, "
        "and MRUK anti-false-positive contracts"
    )


if __name__ == "__main__":
    main()
