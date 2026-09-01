#!/usr/bin/env python3
"""Capture PhraseLayer Listen Mode metrics from an attached Android/Quest device.

This is a local/manual device gate. It does not install or launch the app. Start PhraseLayer on the
headset, run representative utterances, and this tool captures logcat, extracts
PHRASELAYER_LISTEN_METRIC lines, summarizes phase timing, and records device provenance. The report
is evidence only for the device identified in its provenance block.
"""

from __future__ import annotations

import argparse
import datetime as dt
import importlib.util
import json
import pathlib
import subprocess
import sys
import time
from typing import Dict, Iterable, List, Sequence

ROOT = pathlib.Path(__file__).resolve().parents[1]
SUMMARIZER_PATH = ROOT / "tools/summarize_listen_mode_metrics.py"
MARKER = "PHRASELAYER_LISTEN_METRIC"


class CaptureError(RuntimeError):
    pass


def _load_summarizer():
    spec = importlib.util.spec_from_file_location("phrase_layer_listen_metrics", SUMMARIZER_PATH)
    if spec is None or spec.loader is None:
        raise CaptureError("failed to load Listen Mode metrics summarizer")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


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
            raise CaptureError(f"requested adb device {requested!r} is not connected/authorized")
        return requested
    if len(devices) == 1:
        return devices[0]
    if not devices:
        raise CaptureError("no authorized adb device found")
    raise CaptureError("multiple adb devices are connected; pass --serial explicitly")


def metric_lines(lines: Iterable[str]) -> List[str]:
    return [line for line in lines if MARKER in line]


def _run(args: Sequence[str], timeout_seconds: float = 15.0) -> str:
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
        raise CaptureError(
            "command failed: " + " ".join(args) + "\n" + completed.stderr.strip()
        )
    return completed.stdout


def _adb(adb: str, serial: str, *args: str, timeout_seconds: float = 15.0) -> str:
    return _run([adb, "-s", serial, *args], timeout_seconds=timeout_seconds)


def _prop(adb: str, serial: str, name: str) -> str:
    return _adb(adb, serial, "shell", "getprop", name).strip()


def capture_logcat(adb: str, serial: str, duration_seconds: float) -> str:
    if duration_seconds <= 0.0:
        raise CaptureError("capture duration must be positive")
    process = subprocess.Popen(
        [adb, "-s", serial, "logcat", "-v", "threadtime"],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    try:
        try:
            stdout, stderr = process.communicate(timeout=duration_seconds)
        except subprocess.TimeoutExpired:
            process.terminate()
            try:
                stdout, stderr = process.communicate(timeout=5.0)
            except subprocess.TimeoutExpired:
                process.kill()
                stdout, stderr = process.communicate(timeout=5.0)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=5.0)
    if process.returncode not in (0, -15, 143):
        raise CaptureError(f"adb logcat failed with exit {process.returncode}: {stderr.strip()}")
    return stdout


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--adb", default="adb", help="adb executable")
    parser.add_argument("--serial", help="adb device serial; required when multiple devices are connected")
    parser.add_argument("--duration-seconds", type=float, default=60.0)
    parser.add_argument("--output-dir", type=pathlib.Path, required=True)
    parser.add_argument("--clear-logcat", action="store_true")
    args = parser.parse_args()

    devices = parse_adb_devices(_run([args.adb, "devices", "-l"]))
    serial = choose_serial(devices, args.serial)
    if args.clear_logcat:
        _adb(args.adb, serial, "logcat", "-c")

    started_utc = dt.datetime.now(dt.timezone.utc)
    raw = capture_logcat(args.adb, serial, args.duration_seconds)
    finished_utc = dt.datetime.now(dt.timezone.utc)

    args.output_dir.mkdir(parents=True, exist_ok=True)
    raw_path = args.output_dir / "quest-logcat.txt"
    metric_path = args.output_dir / "listen-mode-metrics.log"
    summary_path = args.output_dir / "listen-mode-metrics.json"
    evidence_path = args.output_dir / "listen-mode-device-evidence.json"
    raw_path.write_text(raw, encoding="utf-8")
    filtered = metric_lines(raw.splitlines())
    if not filtered:
        raise CaptureError(
            f"no {MARKER} lines captured; confirm Listen Mode processed at least one utterance"
        )
    metric_path.write_text("\n".join(filtered) + "\n", encoding="utf-8")

    summarizer = _load_summarizer()
    samples = summarizer.parse_lines(filtered)
    summary = summarizer.summarize(samples)
    summary_path.write_text(json.dumps(summary, indent=2, sort_keys=True) + "\n", encoding="utf-8")

    provenance: Dict[str, object] = {
        "schema_version": 1,
        "purpose": "phrase-layer-listen-mode-device-measurement",
        "captured_at_utc": started_utc.isoformat(),
        "finished_at_utc": finished_utc.isoformat(),
        "capture_duration_seconds_requested": args.duration_seconds,
        "adb_serial": serial,
        "device": {
            "manufacturer": _prop(args.adb, serial, "ro.product.manufacturer"),
            "model": _prop(args.adb, serial, "ro.product.model"),
            "device": _prop(args.adb, serial, "ro.product.device"),
            "android_release": _prop(args.adb, serial, "ro.build.version.release"),
            "sdk": _prop(args.adb, serial, "ro.build.version.sdk"),
            "build_fingerprint": _prop(args.adb, serial, "ro.build.fingerprint"),
        },
        "metric_sample_count": summary["sample_count"],
        "phase_timing_coverage": summary["phase_timing_coverage"],
        "files": {
            "raw_logcat": raw_path.name,
            "metric_lines": metric_path.name,
            "summary": summary_path.name,
        },
        "scope": "Measured device evidence only. This report does not establish model quality or numerical parity by itself.",
    }
    evidence_path.write_text(json.dumps(provenance, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"status": "captured", **provenance}, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (CaptureError, subprocess.SubprocessError, OSError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(2)
