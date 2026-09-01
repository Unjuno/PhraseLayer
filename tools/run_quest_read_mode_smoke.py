#!/usr/bin/env python3
"""Install and verify the instrumented PhraseLayer Read Mode fixture on a real Quest 3.

This gate intentionally validates the hardware/visual vertical slice only. The APK uses the explicit
DemoDictionaryFixture translation path, while the device run must prove real passthrough OCR and the
full camera -> OCR -> adaptive plan -> MRUK live-depth fit -> source mask -> Japanese world-text marker.
Raw adb serials are never written to uploaded evidence.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import pathlib
import re
import subprocess
import sys
import time
from typing import Dict, List, Sequence

DEFAULT_PACKAGE = "com.unjuno.phraselayer.readmodefixture"
DEFAULT_EXPECTED_DEVICE_MODEL = "Quest 3"
CAMERA_PERMISSION = "android.permission.CAMERA"
HEADSET_CAMERA_PERMISSION = "horizonos.permission.HEADSET_CAMERA"
OCR_PASS_MARKER = "PhraseLayer Quest OCR smoke test PASS"
READ_MODE_PASS_MARKER = "PhraseLayer Quest Read Mode smoke test PASS"
READ_MODE_TIMEOUT_MARKER = "PhraseLayer Quest Read Mode smoke test FAIL_TIMEOUT"
READ_MODE_EXCEPTION_MARKER = "PhraseLayer Quest Read Mode smoke test FAIL_EXCEPTION"
SURFACE_RUNTIME_MARKER = "surface_runtime=MRUKEnvironmentRaycast"
FATAL_MARKER = "FATAL EXCEPTION"


class SmokeError(RuntimeError):
    pass


def parse_adb_devices(output: str) -> List[str]:
    devices: List[str] = []
    for raw in output.splitlines():
        line = raw.strip()
        if not line or line.startswith("List of devices attached"):
            continue
        fields = line.split()
        if len(fields) >= 2 and fields[1] == "device":
            devices.append(fields[0])
    return devices


def choose_serial(devices: Sequence[str], requested: str | None) -> str:
    if requested:
        if requested not in devices:
            raise SmokeError(f"requested adb device {requested!r} is not connected/authorized")
        return requested
    if len(devices) == 1:
        return devices[0]
    if not devices:
        raise SmokeError("no authorized adb device found")
    raise SmokeError("multiple adb devices are connected; pass --serial explicitly")


def normalize_device_model(value: str) -> str:
    return re.sub(r"[\s_-]+", "", value).casefold()


def require_device_model(actual: str, expected: str) -> None:
    actual = actual.strip()
    expected = expected.strip()
    if not expected:
        raise SmokeError("expected device model must not be empty")
    if normalize_device_model(actual) != normalize_device_model(expected):
        raise SmokeError(
            f"selected adb device is {actual!r}, expected {expected!r}; refusing to claim Quest 3 evidence"
        )


def permission_declared(dumpsys_package: str, permission: str) -> bool:
    return permission in dumpsys_package


def permission_granted(dumpsys_package: str, permission: str) -> bool | None:
    escaped = re.escape(permission)
    explicit = re.search(rf"{escaped}:\s+granted=(true|false)\b", dumpsys_package, re.IGNORECASE)
    if explicit:
        return explicit.group(1).casefold() == "true"
    if permission in dumpsys_package:
        return None
    return None


def readiness_from_logcat(logcat: str) -> Dict[str, bool]:
    return {
        "ocr_smoke_passed": OCR_PASS_MARKER in logcat,
        "read_mode_smoke_passed": READ_MODE_PASS_MARKER in logcat,
        "mruk_environment_raycast_observed": SURFACE_RUNTIME_MARKER in logcat,
        "read_mode_timeout": READ_MODE_TIMEOUT_MARKER in logcat,
        "read_mode_exception": READ_MODE_EXCEPTION_MARKER in logcat,
        "fatal_exception": FATAL_MARKER in logcat,
    }


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def serial_fingerprint(serial: str | None) -> str | None:
    if not serial:
        return None
    return hashlib.sha256(serial.encode("utf-8")).hexdigest()[:12]


def _run(args: Sequence[str], timeout_seconds: float = 30.0) -> str:
    completed = subprocess.run(
        list(args),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout_seconds,
        check=False,
    )
    if completed.returncode != 0:
        raise SmokeError(
            "command failed: " + " ".join(args) + "\n" + completed.stderr.strip()
        )
    return completed.stdout


def _adb(adb: str, serial: str, *args: str, timeout_seconds: float = 30.0) -> str:
    return _run([adb, "-s", serial, *args], timeout_seconds=timeout_seconds)


def _prop(adb: str, serial: str, name: str) -> str:
    return _adb(adb, serial, "shell", "getprop", name).strip()


def resolve_main_activity(adb: str, serial: str, package: str) -> str:
    output = _adb(adb, serial, "shell", "cmd", "package", "resolve-activity", "--brief", package)
    candidates = [line.strip() for line in output.splitlines() if "/" in line]
    if not candidates:
        raise SmokeError(f"unable to resolve launcher activity for {package}")
    return candidates[-1]


def wait_for_pid(adb: str, serial: str, package: str, timeout_seconds: float) -> str:
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        pid = _adb(adb, serial, "shell", "pidof", package).strip()
        if pid:
            return pid.split()[0]
        time.sleep(0.5)
    raise SmokeError(f"package {package} did not start within {timeout_seconds:.1f}s")


def process_logcat(adb: str, serial: str, pid: str) -> str:
    return _adb(adb, serial, "logcat", "--pid", pid, "-d", "-v", "threadtime", timeout_seconds=30.0)


def wait_for_read_mode_pass(
    adb: str,
    serial: str,
    package: str,
    pid: str,
    timeout_seconds: float,
) -> tuple[str, Dict[str, bool]]:
    deadline = time.monotonic() + timeout_seconds
    last_log = ""
    last_readiness = readiness_from_logcat(last_log)
    while time.monotonic() < deadline:
        current_pid = _adb(adb, serial, "shell", "pidof", package).strip()
        if not current_pid:
            raise SmokeError(f"package {package} exited during Read Mode smoke")
        last_log = process_logcat(adb, serial, pid)
        last_readiness = readiness_from_logcat(last_log)
        if last_readiness["fatal_exception"]:
            raise SmokeError("Quest Read Mode logcat contains FATAL EXCEPTION")
        if last_readiness["read_mode_timeout"]:
            raise SmokeError("instrumented Quest Read Mode smoke reported FAIL_TIMEOUT")
        if last_readiness["read_mode_exception"]:
            raise SmokeError("instrumented Quest Read Mode smoke reported FAIL_EXCEPTION")
        if (
            last_readiness["ocr_smoke_passed"]
            and last_readiness["read_mode_smoke_passed"]
            and last_readiness["mruk_environment_raycast_observed"]
        ):
            return last_log, last_readiness
        time.sleep(1.0)
    return last_log, last_readiness


def build_evidence(
    *,
    status: str,
    args: argparse.Namespace,
    started: dt.datetime,
    serial: str | None,
    actual_device_model: str | None,
    activity: str | None,
    pid: str | None,
    camera_declared: bool | None,
    headset_camera_declared: bool | None,
    camera_permission: bool | None,
    headset_camera_permission: bool | None,
    readiness: Dict[str, bool],
    error: BaseException | None = None,
) -> Dict[str, object]:
    finished = dt.datetime.now(dt.timezone.utc)
    device: Dict[str, object] = {"model": actual_device_model or "unobserved"}
    if serial:
        for key, prop in (
            ("manufacturer", "ro.product.manufacturer"),
            ("device", "ro.product.device"),
            ("android_release", "ro.build.version.release"),
            ("sdk", "ro.build.version.sdk"),
            ("build_fingerprint", "ro.build.fingerprint"),
        ):
            try:
                device[key] = _prop(args.adb, serial, prop)
            except (SmokeError, subprocess.SubprocessError, OSError):
                device[key] = "unobserved"

    evidence: Dict[str, object] = {
        "schema_version": 1,
        "purpose": "phrase-layer-quest-read-mode-hardware-visual-smoke",
        "status": status,
        "started_at_utc": started.isoformat(),
        "finished_at_utc": finished.isoformat(),
        "adb_serial_sha256_12": serial_fingerprint(serial),
        "package": args.package,
        "main_activity": activity or "unobserved",
        "pid": int(pid) if pid and pid.isdigit() else None,
        "apk": {
            "file_name": args.apk.name,
            "size_bytes": args.apk.stat().st_size if args.apk.is_file() else 0,
            "sha256": sha256_file(args.apk) if args.apk.is_file() else None,
        },
        "permissions": {
            CAMERA_PERMISSION: {
                "declared": camera_declared,
                "granted": camera_permission,
            },
            HEADSET_CAMERA_PERMISSION: {
                "declared": headset_camera_declared,
                "granted": headset_camera_permission,
            },
        },
        "readiness": readiness,
        "ocr_runtime": "PaddleOCR",
        "surface_runtime": "MRUKEnvironmentRaycast",
        "translation_runtime": "DemoDictionaryFixture",
        "product_translation_gate": False,
        "camera_timestamp_source": "unity-realtime-observation",
        "camera_hardware_timestamp_pose_sync_verified": False,
        "expected_device_model": args.expected_device_model,
        "device": device,
        "files": {"logcat": "quest-read-mode-logcat.txt"},
        "scope": (
            "Real Quest camera/OCR + MRUK live-depth surface fit/tracking + source mask + Japanese world-text vertical slice. "
            "Translation remains the explicit demo dictionary fixture. Camera timestamps remain Unity observation time, "
            "so hardware timestamp/pose synchronization, Marian product translation, visual quality, stereo comfort, "
            "endurance, thermal and performance remain separate gates."
        ),
    }
    if error is not None:
        evidence["failure"] = {
            "type": type(error).__name__,
            "message": str(error),
        }
    return evidence


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--apk", type=pathlib.Path, required=True)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--serial")
    parser.add_argument("--package", default=DEFAULT_PACKAGE)
    parser.add_argument("--expected-device-model", default=DEFAULT_EXPECTED_DEVICE_MODEL)
    parser.add_argument("--smoke-timeout-seconds", type=float, default=120.0)
    parser.add_argument("--output-dir", type=pathlib.Path, required=True)
    args = parser.parse_args()

    if not args.apk.is_file() or args.apk.stat().st_size <= 0:
        raise SmokeError(f"APK is missing or empty: {args.apk}")
    if args.smoke_timeout_seconds <= 0.0:
        raise SmokeError("smoke timeout must be positive")

    args.output_dir.mkdir(parents=True, exist_ok=True)
    log_path = args.output_dir / "quest-read-mode-logcat.txt"
    evidence_path = args.output_dir / "quest-read-mode-smoke.json"
    started = dt.datetime.now(dt.timezone.utc)

    serial: str | None = None
    actual_device_model: str | None = None
    activity: str | None = None
    pid: str | None = None
    camera_declared: bool | None = None
    headset_camera_declared: bool | None = None
    camera_permission: bool | None = None
    headset_camera_permission: bool | None = None
    logcat = ""
    readiness = readiness_from_logcat(logcat)

    try:
        devices = parse_adb_devices(_run([args.adb, "devices", "-l"]))
        serial = choose_serial(devices, args.serial)
        actual_device_model = _prop(args.adb, serial, "ro.product.model")
        require_device_model(actual_device_model, args.expected_device_model)

        _adb(args.adb, serial, "install", "-r", "-g", str(args.apk), timeout_seconds=180.0)
        package_path = _adb(args.adb, serial, "shell", "pm", "path", args.package).strip()
        if not package_path.startswith("package:"):
            raise SmokeError(f"package manager did not report installed package {args.package}")

        package_dump = _adb(args.adb, serial, "shell", "dumpsys", "package", args.package)
        camera_declared = permission_declared(package_dump, CAMERA_PERMISSION)
        headset_camera_declared = permission_declared(package_dump, HEADSET_CAMERA_PERMISSION)
        if not camera_declared:
            raise SmokeError(f"installed APK does not declare required permission {CAMERA_PERMISSION}")
        if not headset_camera_declared:
            raise SmokeError(f"installed APK does not declare required permission {HEADSET_CAMERA_PERMISSION}")

        camera_permission = permission_granted(package_dump, CAMERA_PERMISSION)
        headset_camera_permission = permission_granted(package_dump, HEADSET_CAMERA_PERMISSION)
        if camera_permission is False:
            raise SmokeError(f"{CAMERA_PERMISSION} is explicitly denied after adb install -g")
        if headset_camera_permission is False:
            raise SmokeError(f"{HEADSET_CAMERA_PERMISSION} is explicitly denied after adb install -g")

        _adb(args.adb, serial, "logcat", "-c")
        _adb(args.adb, serial, "shell", "am", "force-stop", args.package)
        activity = resolve_main_activity(args.adb, serial, args.package)
        _adb(args.adb, serial, "shell", "am", "start", "-W", "-n", activity, timeout_seconds=60.0)
        pid = wait_for_pid(args.adb, serial, args.package, min(30.0, args.smoke_timeout_seconds))

        logcat, readiness = wait_for_read_mode_pass(
            args.adb,
            serial,
            args.package,
            pid,
            args.smoke_timeout_seconds,
        )
        required = (
            "ocr_smoke_passed",
            "read_mode_smoke_passed",
            "mruk_environment_raycast_observed",
        )
        if not all(readiness[name] for name in required):
            missing = [name for name in required if not readiness[name]]
            raise SmokeError("Quest Read Mode PASS evidence missing: " + ", ".join(missing))

        evidence = build_evidence(
            status="pass",
            args=args,
            started=started,
            serial=serial,
            actual_device_model=actual_device_model,
            activity=activity,
            pid=pid,
            camera_declared=camera_declared,
            headset_camera_declared=headset_camera_declared,
            camera_permission=camera_permission,
            headset_camera_permission=headset_camera_permission,
            readiness=readiness,
        )
        evidence_path.write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        print(json.dumps(evidence, sort_keys=True))
    except (SmokeError, subprocess.SubprocessError, OSError, ValueError) as error:
        if serial and pid:
            try:
                latest_log = process_logcat(args.adb, serial, pid)
                if latest_log:
                    logcat = latest_log
                    readiness = readiness_from_logcat(logcat)
            except (SmokeError, subprocess.SubprocessError, OSError):
                pass
        evidence = build_evidence(
            status="fail",
            args=args,
            started=started,
            serial=serial,
            actual_device_model=actual_device_model,
            activity=activity,
            pid=pid,
            camera_declared=camera_declared,
            headset_camera_declared=headset_camera_declared,
            camera_permission=camera_permission,
            headset_camera_permission=headset_camera_permission,
            readiness=readiness,
            error=error,
        )
        evidence_path.write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        raise
    finally:
        log_path.write_text(logcat, encoding="utf-8")


if __name__ == "__main__":
    try:
        main()
    except (SmokeError, subprocess.SubprocessError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)
