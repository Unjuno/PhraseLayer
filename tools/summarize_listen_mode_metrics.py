#!/usr/bin/env python3
"""Summarize machine-readable PhraseLayer Listen Mode device timing lines.

Expected input is captured Unity/Android log output containing lines emitted by
UnityLiveListenModeBehaviour with the PHRASELAYER_LISTEN_METRIC marker. This tool does not claim
that a log came from Quest 3; provenance must be supplied by the measurement run/evidence record.
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
        sample = {
            "utterance": int(match.group("utterance")),
            "audio_s": float(match.group("audio_s")),
            "pipeline_ms": float(match.group("pipeline_ms")),
            "processing_to_audio": float(match.group("ratio")),
            "transcript_chars": int(match.group("chars")),
            "adaptive_plan": match.group("plan") == "1",
        }
        if sample["audio_s"] <= 0.0:
            raise MetricsError(f"audio_s must be positive at input line {line_number}")
        if sample["pipeline_ms"] < 0.0 or sample["processing_to_audio"] < 0.0:
            raise MetricsError(f"timing values must be non-negative at input line {line_number}")
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


def summarize(samples: List[Dict[str, object]]) -> Dict[str, object]:
    if not samples:
        raise MetricsError(f"no {MARKER} samples found")
    utterances = [int(sample["utterance"]) for sample in samples]
    if len(set(utterances)) != len(utterances):
        raise MetricsError("duplicate utterance ids found in metric log")

    pipeline = [float(sample["pipeline_ms"]) for sample in samples]
    ratio = [float(sample["processing_to_audio"]) for sample in samples]
    audio = [float(sample["audio_s"]) for sample in samples]
    return {
        "schema_version": 1,
        "metric": "listen-mode-end-to-end-pipeline",
        "sample_count": len(samples),
        "audio_seconds": {
            "total": sum(audio),
            "median": statistics.median(audio),
            "maximum": max(audio),
        },
        "pipeline_milliseconds": {
            "median": statistics.median(pipeline),
            "p95_nearest_rank": percentile_nearest_rank(pipeline, 95.0),
            "maximum": max(pipeline),
        },
        "processing_to_audio_ratio": {
            "median": statistics.median(ratio),
            "p95_nearest_rank": percentile_nearest_rank(ratio, 95.0),
            "maximum": max(ratio),
        },
        "adaptive_plan_sample_count": sum(1 for sample in samples if bool(sample["adaptive_plan"])),
        "transcript_character_count": sum(int(sample["transcript_chars"]) for sample in samples),
        "samples": samples,
        "scope": "End-to-end Listen Mode submission timing (ASR plus adaptive language planning/translation), not ASR-only latency.",
    }


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
