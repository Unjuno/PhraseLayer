#!/usr/bin/env python3
"""Summarize machine-readable PhraseLayer Listen Mode device timing lines.

Expected input is captured Unity/Android log output containing lines emitted by
UnityLiveListenModeBehaviour with the PHRASELAYER_LISTEN_METRIC marker. New logs separate ASR,
adaptive-language planning/translation, Core total, and Unity submission time. Legacy pre-phase logs
remain parseable so old measurements do not become unreadable. This tool does not claim that a log
came from Quest 3; provenance must be supplied by the measurement run/evidence record.
"""

from __future__ import annotations

import argparse
import json
import math
import pathlib
import re
import statistics
from typing import Dict, Iterable, List

MARKER = "PHRASELAYER_LISTEN_METRIC"
PATTERN = re.compile(
    r"PHRASELAYER_LISTEN_METRIC\s+"
    r"utterance=(?P<utterance>\d+)\s+"
    r"audio_s=(?P<audio_s>\d+(?:\.\d+)?)\s+"
    r"(?:asr_ms=(?P<asr_ms>\d+(?:\.\d+)?)\s+"
    r"plan_ms=(?P<plan_ms>\d+(?:\.\d+)?)\s+"
    r"core_ms=(?P<core_ms>\d+(?:\.\d+)?)\s+)?"
    r"pipeline_ms=(?P<pipeline_ms>\d+(?:\.\d+)?)\s+"
    r"processing_to_audio=(?P<ratio>\d+(?:\.\d+)?)\s+"
    r"transcript_chars=(?P<chars>\d+)\s+"
    r"adaptive_plan=(?P<plan>[01])"
)


class MetricsError(ValueError):
    pass


def parse_lines(lines: Iterable[str]) -> List[Dict[str, object]]:
    samples: List[Dict[str, object]] = []
    for line_number, line in enumerate(lines, start=1):
        if MARKER not in line:
            continue
        match = PATTERN.search(line)
        if match is None:
            raise MetricsError(f"malformed {MARKER} line at input line {line_number}")
        audio_s = float(match.group("audio_s"))
        phase_values = {
            "asr_ms": float(match.group("asr_ms")) if match.group("asr_ms") is not None else None,
            "plan_ms": float(match.group("plan_ms")) if match.group("plan_ms") is not None else None,
            "core_ms": float(match.group("core_ms")) if match.group("core_ms") is not None else None,
        }
        sample: Dict[str, object] = {
            "utterance": int(match.group("utterance")),
            "audio_s": audio_s,
            "pipeline_ms": float(match.group("pipeline_ms")),
            "processing_to_audio": float(match.group("ratio")),
            "transcript_chars": int(match.group("chars")),
            "adaptive_plan": match.group("plan") == "1",
            **phase_values,
        }
        if audio_s <= 0.0:
            raise MetricsError(f"audio_s must be positive at input line {line_number}")
        if sample["pipeline_ms"] < 0.0 or sample["processing_to_audio"] < 0.0:
            raise MetricsError(f"timing values must be non-negative at input line {line_number}")
        present_phases = [value is not None for value in phase_values.values()]
        if any(present_phases) and not all(present_phases):
            raise MetricsError(f"phase timing fields must be all present or all absent at input line {line_number}")
        if all(present_phases):
            asr_ms = float(phase_values["asr_ms"])
            plan_ms = float(phase_values["plan_ms"])
            core_ms = float(phase_values["core_ms"])
            if asr_ms < 0.0 or plan_ms < 0.0 or core_ms < 0.0:
                raise MetricsError(f"phase timing values must be non-negative at input line {line_number}")
            if core_ms + 0.01 < asr_ms or core_ms + 0.01 < plan_ms:
                raise MetricsError(f"core_ms cannot be smaller than a measured phase at input line {line_number}")
            if float(sample["pipeline_ms"]) + 0.01 < core_ms:
                raise MetricsError(f"pipeline_ms cannot be smaller than core_ms at input line {line_number}")
            sample["asr_to_audio"] = (asr_ms / 1000.0) / audio_s
            sample["plan_to_audio"] = (plan_ms / 1000.0) / audio_s
            sample["core_to_audio"] = (core_ms / 1000.0) / audio_s
        samples.append(sample)
    return samples


def percentile_nearest_rank(values: List[float], percentile: float) -> float:
    if not values:
        raise MetricsError("cannot calculate percentile for an empty sample set")
    if percentile <= 0.0 or percentile > 100.0:
        raise MetricsError("percentile must be in (0,100]")
    ordered = sorted(values)
    rank = max(1, math.ceil((percentile / 100.0) * len(ordered)))
    return ordered[rank - 1]


def _stats(values: List[float]) -> Dict[str, float]:
    return {
        "median": statistics.median(values),
        "p95_nearest_rank": percentile_nearest_rank(values, 95.0),
        "maximum": max(values),
    }


def summarize(samples: List[Dict[str, object]]) -> Dict[str, object]:
    if not samples:
        raise MetricsError(f"no {MARKER} samples found")
    utterances = [int(sample["utterance"]) for sample in samples]
    if len(set(utterances)) != len(utterances):
        raise MetricsError("duplicate utterance ids found in metric log")

    pipeline = [float(sample["pipeline_ms"]) for sample in samples]
    ratio = [float(sample["processing_to_audio"]) for sample in samples]
    audio = [float(sample["audio_s"]) for sample in samples]
    phase_complete = all(sample.get("asr_ms") is not None for sample in samples)
    phase_absent = all(sample.get("asr_ms") is None for sample in samples)
    if not phase_complete and not phase_absent:
        raise MetricsError("cannot summarize a mixed legacy/phase-timed sample set")

    report: Dict[str, object] = {
        "schema_version": 2,
        "metric": "listen-mode-end-to-end-pipeline",
        "sample_count": len(samples),
        "phase_timing_coverage": "complete" if phase_complete else "legacy-none",
        "audio_seconds": {
            "total": sum(audio),
            "median": statistics.median(audio),
            "maximum": max(audio),
        },
        "pipeline_milliseconds": _stats(pipeline),
        "processing_to_audio_ratio": _stats(ratio),
        "adaptive_plan_sample_count": sum(1 for sample in samples if bool(sample["adaptive_plan"])),
        "transcript_character_count": sum(int(sample["transcript_chars"]) for sample in samples),
        "samples": samples,
        "scope": "Listen Mode submission timing. When phase timing is complete, ASR and adaptive-language planning/translation are reported separately; GPU/native memory is not measured here.",
    }
    if phase_complete:
        asr = [float(sample["asr_ms"]) for sample in samples]
        planning = [float(sample["plan_ms"]) for sample in samples]
        core = [float(sample["core_ms"]) for sample in samples]
        asr_ratio = [float(sample["asr_to_audio"]) for sample in samples]
        plan_ratio = [float(sample["plan_to_audio"]) for sample in samples]
        core_ratio = [float(sample["core_to_audio"]) for sample in samples]
        report["asr_milliseconds"] = _stats(asr)
        report["language_plan_milliseconds"] = _stats(planning)
        report["core_processing_milliseconds"] = _stats(core)
        report["asr_to_audio_ratio"] = _stats(asr_ratio)
        report["language_plan_to_audio_ratio"] = _stats(plan_ratio)
        report["core_to_audio_ratio"] = _stats(core_ratio)
    return report


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    samples = parse_lines(args.input.read_text(encoding="utf-8", errors="replace").splitlines())
    report = summarize(samples)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in report.items() if key != "samples"}, sort_keys=True))


if __name__ == "__main__":
    main()
