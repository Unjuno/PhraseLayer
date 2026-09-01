#!/usr/bin/env python3
"""Install and launch a PhraseLayer Android APK on an attached Quest/Android device.

This is a real-device smoke gate, not a performance benchmark. It verifies the package installs,
RECORD_AUDIO is granted, the launcher starts, the process remains alive, and startup logcat proves both
offline model stacks initialized: Marian translation plus Moonshine Listen Mode. Utterance latency/RTF is
captured separately by capture_quest_listen_mode_metrics.py so a startup smoke cannot be mistaken for
performance evidence.
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

DEFAULT_PACKAGE = "com.unjuno.phraselayer"
MICROPHONE_MARKER = "Microphone capture started:"
MARIAN_READY_MARKER = "Marian offline translation ready:"
LISTEN_READY_MARKER = "Listen Mode ready: microphone -> Moonshine ASR -> adaptive language plan."
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


def record_audio_granted(dumpsys_package: str) -> bool:
    pattern = re.compile(r"android\.permission\.RECORD_AUDIO:\s+granted=true\b", re.IGNORECASE)
    return pattern.search(dumpsys_package) is not None


def readiness_from_logcat(logcat: str) -> Dict[str, bool]:
    return {
        "microphone_started": MICROPHONE_MARKER in logcat,
        "marian_translation_ready": MARIAN_READY_MARKER in logcat,
        "listen_mode_ready": LISTEN_READY_MARKER in logcat,
        "fatal_exception": FATAL_MARKER in logcat,
    }


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


def wait_for_readiness(
    adb: str,
    serial: str,
    package: str,
    pid: str,
    timeout_seconds: float,
) -> tuple[str, Dict[str, bool]]:
    deadline = time.monotonic() + timeout_seconds
    last_log = ""
    while time.monotonic() < deadline:
        current_pid = _adb(adb, serial, "shell", "pidof", package).strip()
        if not current_pid:
            raise SmokeError(f"package {package} exited during startup")
        last_log = process_logcat(adb, serial, pid)
        readiness = readiness_from_logcat(last_log)
        if readiness["fatal_exception"]:
            raise SmokeError("startup logcat contains FATAL EXCEPTION")
        if (
            readiness["microphone_started"]
            and readiness["marian_translation_ready"]
            and readiness["listen_mode_ready"]
        ):
            return last_log, readiness
        time.sleep(1.0)
    return last_log, readiness_from_logcat(last_log)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--apk", type=pathlib.Path, required=True)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--serial")
    parser.add_argument("--package", default=DEFAULT_PACKAGE)
    parser.add_argument("--startup-timeout-seconds", type=float, default=90.0)
    parser.add_argument("--output-dir", type=pathlib.Path, required=True)
    args = parser.parse_args()

    if not args.apk.is_file() or args.apk.stat().st_size <= 0:
        raise SmokeError(f"APK is missing or empty: {args.apk}")
    if args.startup_timeout_seconds <= 0.0:
        raise SmokeError("startup timeout must be positive")

    devices = parse_adb_devices(_run([args.adb, "devices", "-l"]))
    serial = choose_serial(devices, args.serial)
    started = dt.datetime.now(dt.timezone.utc)

    _adb(args.adb, serial, "install", "-r", "-g", str(args.apk), timeout_seconds=180.0)
    package_path = _adb(args.adb, serial, "shell", "pm", "path", args.package).strip()
    if not package_path.startswith("package:"):
        raise SmokeError(f"package manager did not report installed package {args.package}")

    package_dump = _adb(args.adb, serial, "shell", "dumpsys", "package", args.package)
    microphone_granted = record_audio_granted(package_dump)
    if not microphone_granted:
        raise SmokeError("android.permission.RECORD_AUDIO is not granted after adb install -g")

    _adb(args.adb, serial, "logcat", "-c")
    _adb(args.adb, serial, "shell", "am", "force-stop", args.package)
    activity = resolve_main_activity(args.adb, serial, args.package)
    _adb(args.adb, serial, "shell", "am", "start", "-W", "-n", activity, timeout_seconds=60.0)
    pid = wait_for_pid(args.adb, serial, args.package, min(30.0, args.startup_timeout_seconds))
    logcat, readiness = wait_for_readiness(
        args.adb,
        serial,
        args.package,
        pid,
        args.startup_timeout_seconds,
    )
    if not (
        readiness["microphone_started"]
        and readiness["marian_translation_ready"]
        and readiness["listen_mode_ready"]
    ):
        missing = [name for name, value in readiness.items() if name != "fatal_exception" and not value]
        raise SmokeError("Listen Mode startup readiness markers missing: " + ", ".join(missing))

    finished = dt.datetime.now(dt.timezone.utc)
    args.output_dir.mkdir(parents=True, exist_ok=True)
    log_path = args.output_dir / "quest-startup-logcat.txt"
    evidence_path = args.output_dir / "quest-listen-mode-smoke.json"
    log_path.write_text(logcat, encoding="utf-8")

    evidence: Dict[str, object] = {
        "schema_version": 1,
        "purpose": "phrase-layer-quest-listen-mode-startup-smoke",
        "started_at_utc": started.isoformat(),
        "finished_at_utc": finished.isoformat(),
        "adb_serial": serial,
        "package": args.package,
        "main_activity": activity,
        "pid": int(pid),
        "apk": {
            "file_name": args.apk.name,
            "size_bytes": args.apk.stat().st_size,
            "sha256": sha256_file(args.apk),
        },
        "record_audio_granted": microphone_granted,
        "readiness": readiness,
        "offline_translation_runtime": "Marian",
        "offline_asr_runtime": "MoonshineV1",
        "device": {
            "manufacturer": _prop(args.adb, serial, "ro.product.manufacturer"),
            "model": _prop(args.adb, serial, "ro.product.model"),
            "device": _prop(args.adb, serial, "ro.product.device"),
            "android_release": _prop(args.adb, serial, "ro.build.version.release"),
            "sdk": _prop(args.adb, serial, "ro.build.version.sdk"),
            "build_fingerprint": _prop(args.adb, serial, "ro.build.fingerprint"),
        },
        "files": {"startup_logcat": log_path.name},
        "scope": "Startup smoke for Marian + Moonshine initialization only. Transcript correctness and Quest latency/memory/thermal remain separate gates.",
    }
    evidence_path.write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"status": "pass", **evidence}, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (SmokeError, subprocess.SubprocessError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)
