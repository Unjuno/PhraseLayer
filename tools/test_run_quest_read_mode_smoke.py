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

    empty = module.readiness_from_logcat("")
    assert empty == {
        "ocr_smoke_passed": False,
        "read_mode_smoke_passed": False,
        "mruk_environment_raycast_observed": False,
        "read_mode_timeout": False,
        "read_mode_exception": False,
        "fatal_exception": False,
    }

    passed = module.readiness_from_logcat(
        "PhraseLayer Quest OCR smoke test PASS\n"
        "PhraseLayer Quest Read Mode smoke test PASS\n"
        "surface_runtime=MRUKEnvironmentRaycast environment_abi_validated=true\n"
    )
    assert passed["ocr_smoke_passed"] is True
    assert passed["read_mode_smoke_passed"] is True
    assert passed["mruk_environment_raycast_observed"] is True
    assert passed["fatal_exception"] is False

    incomplete = module.readiness_from_logcat(
        "PhraseLayer Quest OCR smoke test PASS\n"
        "PhraseLayer Quest Read Mode smoke test PASS\n"
    )
    assert incomplete["mruk_environment_raycast_observed"] is False

    failed = module.readiness_from_logcat(
        "PhraseLayer Quest Read Mode smoke test FAIL_TIMEOUT\nFATAL EXCEPTION\n"
    )
    assert failed["read_mode_timeout"] is True
    assert failed["fatal_exception"] is True

    print(
        "PASS: Quest Read Mode device smoke parsing, Quest identity, camera permission declaration, "
        "and MRUK anti-false-positive contracts"
    )


if __name__ == "__main__":
    main()
