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
        module.SmokeError("command failed: adb -s 1WMHH000000000 shell pidof app"),
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
        "read_mode_timeout": False,
        "read_mode_exception": False,
        "fatal_exception": False,
    }

    passed = module.readiness_from_logcat(
        "PhraseLayer Quest OCR smoke test PASS\n"
        "PhraseLayer Quest Read Mode smoke test PASS\n"
        "camera_timestamp_source=MetaPassthroughCameraAccess.Timestamp captured_pose_projection=true captured_pose_rays=5\n"
        "surface_runtime=MRUKEnvironmentRaycast environment_abi_validated=true\n"
    )
    assert passed["ocr_smoke_passed"] is True
    assert passed["read_mode_smoke_passed"] is True
    assert passed["mruk_environment_raycast_observed"] is True
    assert passed["captured_pose_projection_observed"] is True
    assert passed["fatal_exception"] is False

    incomplete = module.readiness_from_logcat(
        "PhraseLayer Quest OCR smoke test PASS\n"
        "PhraseLayer Quest Read Mode smoke test PASS\n"
        "surface_runtime=MRUKEnvironmentRaycast environment_abi_validated=true\n"
    )
    assert incomplete["mruk_environment_raycast_observed"] is True
    assert incomplete["captured_pose_projection_observed"] is False

    failed = module.readiness_from_logcat(
        "PhraseLayer Quest Read Mode smoke test FAIL_TIMEOUT\nFATAL EXCEPTION\n"
    )
    assert failed["read_mode_timeout"] is True
    assert failed["fatal_exception"] is True

    raw_log = (
        "09-02 10:00:00.000 123 123 I Unity : PhraseLayer Quest OCR smoke test PASS\n"
        "09-02 10:00:00.001 123 123 I Unity : regions=1 overall_confidence=0.99 text_length=14\n"
        "09-02 10:00:00.002 123 123 I Unity : recognized_text=PRIVATE STREET SIGN\n"
        "09-02 10:00:00.003 123 123 I Unity : display_text=秘密の表示\n"
        "09-02 10:00:00.004 123 123 I Other : arbitrary user content SECRET-123\n"
        "09-02 10:00:00.005 123 123 I Unity : camera_timestamp_source=MetaPassthroughCameraAccess.Timestamp captured_pose_projection=true captured_pose_rays=5\n"
        "09-02 10:00:00.006 123 123 I Unity : surface_runtime=MRUKEnvironmentRaycast environment_abi_validated=true\n"
        "09-02 10:00:00.007 123 123 E AndroidRuntime : FATAL EXCEPTION: main private-stack-text\n"
    )
    sanitized = module.sanitize_logcat_diagnostics(raw_log)
    assert "PhraseLayer Quest OCR smoke test PASS" in sanitized
    assert "regions=1 overall_confidence=0.99 text_length=14" in sanitized
    assert "camera_timestamp_source=MetaPassthroughCameraAccess.Timestamp" in sanitized
    assert "surface_runtime=MRUKEnvironmentRaycast" in sanitized
    assert "FATAL EXCEPTION\n" in sanitized
    assert "PRIVATE STREET SIGN" not in sanitized
    assert "秘密の表示" not in sanitized
    assert "SECRET-123" not in sanitized
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
    assert 'Graphics.Blit/readback preprocessing path' not in source
    assert '"camera_timestamp_source": "MetaPassthroughCameraAccess.Timestamp"' in source
    assert '"camera_pose_source": "MetaPassthroughCameraAccess.GetCameraPose"' in source
    assert '"captured_pose_projection_required": True' in source
    assert '"camera_pixel_pose_sync_verified": False' in source
    assert '"raw_process_logcat_written_to_disk": False' in source
    assert '"raw_process_logcat_uploaded": False' in source
    assert '"recognized_text_allowed_in_diagnostics": False' in source
    assert 'diagnostics_path.write_text(sanitize_logcat_diagnostics(logcat)' in source
    assert 'quest-read-mode-logcat.txt' not in source

    print(
        "PASS: Quest Read Mode device smoke parsing, permission declaration, redacted device identity/failures, "
        "allowlisted diagnostics, GPU OCR preprocessing evidence, captured camera-pose requirement, and MRUK anti-false-positive contracts"
    )


if __name__ == "__main__":
    main()
