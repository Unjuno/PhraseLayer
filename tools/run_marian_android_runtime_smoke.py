#!/usr/bin/env python3
"""Run the deterministic PhraseLayer Marian product-translation fixture on an ARM64 Android device.

This is deliberately not a Quest gate. It proves Android/IL2CPP runtime initialization of the staged managed tokenizer,
Unity Inference Marian backend, and semantic-span LanguagePipeline against the offline reference fixture. Raw adb serials
and raw process logcat are never persisted.
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

DEFAULT_PACKAGE = "com.unjuno.phraselayer.marianfixture"
PASS_MARKER = "PhraseLayer Marian Android runtime smoke PASS"
FAIL_MARKER = "PhraseLayer Marian Android runtime smoke FAIL_EXCEPTION"
REFERENCE_MARKER = "reference_match=true"
PRODUCT_GATE_MARKER = "product_translation_gate=true"
BACKEND_MARKER = "generation_backend=UnityMarianDeviceResidentGenerationBackend"
FATAL_MARKER = "FATAL EXCEPTION"
DIAGNOSTIC_FILE_NAME = "marian-android-runtime-diagnostics.txt"
DIAGNOSTIC_START_MARKERS = (
    "PhraseLayer Marian Android runtime smoke ",
    "elapsed_ms=",
    "translation_runtime=",
    "fixture_source=",
    "failure_type=",
)
_NUM = r"[0-9]+(?:\.[0-9]+)?"
_TOKEN = r"[A-Za-z0-9_.-]+"
SAFE_DIAGNOSTIC_PATTERNS = tuple(
    re.compile(pattern)
    for pattern in (
        r"^PhraseLayer Marian Android runtime smoke (?:PASS|FAIL_EXCEPTION)$",
        rf"^elapsed_ms={_NUM} bootstrap_ready=true translation_override=true assisted_units=[0-9]+ segments=[0-9]+ reference_match=(?:true|false) display_length=[0-9]+$",
        r"^translation_runtime=MarianOpusMtEnJa generation_backend=UnityMarianDeviceResidentGenerationBackend tokenizer_runtime=Microsoft\.ML\.Tokenizers semantic_span_pipeline=true product_translation_gate=true$",
        r"^fixture_source=keep-off translated_text=<redacted; exact offline reference match required>$",
        rf"^failure_type={_TOKEN}$",
    )
)


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
            raise SmokeError("requested adb device is not connected/authorized")
        return requested
    if len(devices) == 1:
        return devices[0]
    if not devices:
        raise SmokeError("no authorized adb device found")
    raise SmokeError("multiple adb devices are connected; pass --serial explicitly")


def parse_abis(value: str) -> List[str]:
    return [item.strip() for item in value.split(",") if item.strip()]


def require_arm64_abi(abilist: str, primary_abi: str) -> List[str]:
    abis = parse_abis(abilist)
    if not abis and primary_abi.strip():
        abis = [primary_abi.strip()]
    if "arm64-v8a" not in abis:
        raise SmokeError("selected Android device does not advertise arm64-v8a support")
    return abis


def readiness_from_logcat(logcat: str) -> Dict[str, bool]:
    return {
        "runtime_smoke_passed": PASS_MARKER in logcat,
        "runtime_smoke_failed": FAIL_MARKER in logcat,
        "exact_reference_match_observed": REFERENCE_MARKER in logcat,
        "product_translation_gate_observed": PRODUCT_GATE_MARKER in logcat,
        "device_resident_backend_observed": BACKEND_MARKER in logcat,
        "fatal_exception": FATAL_MARKER in logcat,
    }


def sanitize_logcat_diagnostics(logcat: str) -> str:
    safe_lines: List[str] = []
    for raw in logcat.splitlines():
        if FATAL_MARKER in raw:
            safe_lines.append(FATAL_MARKER)
            continue
        candidate = None
        for marker in DIAGNOSTIC_START_MARKERS:
            index = raw.find(marker)
            if index >= 0:
                candidate = raw[index:].strip()
                break
        if candidate is None:
            continue
        if any(pattern.fullmatch(candidate) for pattern in SAFE_DIAGNOSTIC_PATTERNS):
            safe_lines.append(candidate)
    return "" if not safe_lines else "\n".join(safe_lines) + "\n"


def serial_fingerprint(serial: str | None) -> str | None:
    if not serial:
        return None
    return hashlib.sha256(serial.encode("utf-8")).hexdigest()[:12]


def redact_failure_message(error: BaseException, serial: str | None) -> str:
    message = str(error)
    if serial:
        message = message.replace(serial, "<redacted-adb-serial>")
    return message


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


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
        raise SmokeError("command failed: " + " ".join(args) + "\n" + completed.stderr.strip())
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


def wait_for_pass(
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
            raise SmokeError(f"package {package} exited during Marian Android runtime smoke")
        last_log = process_logcat(adb, serial, pid)
        last_readiness = readiness_from_logcat(last_log)
        if last_readiness["fatal_exception"]:
            raise SmokeError("Marian Android runtime logcat contains FATAL EXCEPTION")
        if last_readiness["runtime_smoke_failed"]:
            raise SmokeError("instrumented Marian Android runtime smoke reported FAIL_EXCEPTION")
        if (
            last_readiness["runtime_smoke_passed"]
            and last_readiness["exact_reference_match_observed"]
            and last_readiness["product_translation_gate_observed"]
            and last_readiness["device_resident_backend_observed"]
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
    model: str | None,
    manufacturer: str | None,
    abis: Sequence[str],
    android_release: str | None,
    sdk: str | None,
    activity: str | None,
    readiness: Dict[str, bool],
    error: BaseException | None = None,
) -> Dict[str, object]:
    evidence: Dict[str, object] = {
        "schema_version": 1,
        "purpose": "phrase-layer-marian-android-runtime-smoke",
        "status": status,
        "started_at_utc": started.isoformat(),
        "finished_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "adb_serial_sha256_12": serial_fingerprint(serial),
        "package": args.package,
        "main_activity": activity or "unobserved",
        "apk": {
            "file_name": args.apk.name,
            "size_bytes": args.apk.stat().st_size if args.apk.is_file() else 0,
            "sha256": sha256_file(args.apk) if args.apk.is_file() else None,
        },
        "device": {
            "model": model or "unobserved",
            "manufacturer": manufacturer or "unobserved",
            "abis": list(abis),
            "android_release": android_release or "unobserved",
            "sdk": sdk or "unobserved",
        },
        "readiness": readiness,
        "translation_runtime": "MarianOpusMtEnJa",
        "generation_backend": "UnityMarianDeviceResidentGenerationBackend",
        "tokenizer_runtime": "Microsoft.ML.Tokenizers",
        "fixture_source": "keep off",
        "semantic_span_pipeline": True,
        "exact_offline_reference_match_required": True,
        "product_translation_gate": True,
        "android_runtime_execution_performed": True,
        "quest_device_execution_performed": False,
        "network_required": False,
        "log_privacy": {
            "raw_process_logcat_written_to_disk": False,
            "raw_process_logcat_uploaded": False,
            "diagnostic_lines_require_full_grammar_match": True,
            "translated_text_allowed_in_diagnostics": False,
        },
        "files": {"sanitized_diagnostics": DIAGNOSTIC_FILE_NAME},
    }
    if error is not None:
        evidence["failure"] = {
            "type": type(error).__name__,
            "message": redact_failure_message(error, serial),
        }
    return evidence


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--apk", type=pathlib.Path, required=True)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--serial")
    parser.add_argument("--package", default=DEFAULT_PACKAGE)
    parser.add_argument("--smoke-timeout-seconds", type=float, default=180.0)
    parser.add_argument("--output-dir", type=pathlib.Path, required=True)
    parser.add_argument("--uninstall-after", action="store_true")
    args = parser.parse_args()

    if not args.apk.is_file() or args.apk.stat().st_size <= 0:
        raise SmokeError(f"APK is missing or empty: {args.apk}")
    if args.smoke_timeout_seconds <= 0.0:
        raise SmokeError("smoke timeout must be positive")

    args.output_dir.mkdir(parents=True, exist_ok=True)
    diagnostics_path = args.output_dir / DIAGNOSTIC_FILE_NAME
    evidence_path = args.output_dir / "marian-android-runtime-smoke.json"
    started = dt.datetime.now(dt.timezone.utc)

    serial: str | None = None
    model: str | None = None
    manufacturer: str | None = None
    android_release: str | None = None
    sdk: str | None = None
    abis: List[str] = []
    activity: str | None = None
    pid: str | None = None
    logcat = ""
    readiness = readiness_from_logcat(logcat)

    try:
        serial = choose_serial(parse_adb_devices(_run([args.adb, "devices", "-l"])), args.serial)
        model = _prop(args.adb, serial, "ro.product.model")
        manufacturer = _prop(args.adb, serial, "ro.product.manufacturer")
        android_release = _prop(args.adb, serial, "ro.build.version.release")
        sdk = _prop(args.adb, serial, "ro.build.version.sdk")
        abis = require_arm64_abi(
            _prop(args.adb, serial, "ro.product.cpu.abilist"),
            _prop(args.adb, serial, "ro.product.cpu.abi"),
        )

        _adb(args.adb, serial, "install", "-r", str(args.apk), timeout_seconds=180.0)
        package_path = _adb(args.adb, serial, "shell", "pm", "path", args.package).strip()
        if not package_path.startswith("package:"):
            raise SmokeError(f"package manager did not report installed package {args.package}")

        _adb(args.adb, serial, "logcat", "-c")
        _adb(args.adb, serial, "shell", "am", "force-stop", args.package)
        activity = resolve_main_activity(args.adb, serial, args.package)
        _adb(args.adb, serial, "shell", "am", "start", "-W", "-n", activity, timeout_seconds=60.0)
        pid = wait_for_pid(args.adb, serial, args.package, min(30.0, args.smoke_timeout_seconds))
        logcat, readiness = wait_for_pass(args.adb, serial, args.package, pid, args.smoke_timeout_seconds)

        required = (
            "runtime_smoke_passed",
            "exact_reference_match_observed",
            "product_translation_gate_observed",
            "device_resident_backend_observed",
        )
        if not all(readiness[name] for name in required):
            missing = [name for name in required if not readiness[name]]
            raise SmokeError("Marian Android runtime PASS evidence missing: " + ", ".join(missing))

        evidence = build_evidence(
            status="pass",
            args=args,
            started=started,
            serial=serial,
            model=model,
            manufacturer=manufacturer,
            abis=abis,
            android_release=android_release,
            sdk=sdk,
            activity=activity,
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
            model=model,
            manufacturer=manufacturer,
            abis=abis,
            android_release=android_release,
            sdk=sdk,
            activity=activity,
            readiness=readiness,
            error=error,
        )
        evidence_path.write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        raise
    finally:
        diagnostics_path.write_text(sanitize_logcat_diagnostics(logcat), encoding="utf-8")
        if args.uninstall_after and serial:
            try:
                _adb(args.adb, serial, "uninstall", args.package, timeout_seconds=60.0)
            except (SmokeError, subprocess.SubprocessError, OSError):
                pass


if __name__ == "__main__":
    try:
        main()
    except (SmokeError, subprocess.SubprocessError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)
