#!/usr/bin/env python3
"""Stress Paddle DB box-score accept/reject parity around the 0.4 production threshold.

This is a host-only differential experiment. It intentionally disables the box-score threshold while
collecting Paddle/OpenCV and production-Core candidate scores, then applies the real 0.4 threshold to
both score streams offline. Any disagreement is a real detector candidate decision flip.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess

import cv2
import numpy as np

import run_ppocr_detector_db_differential as oracle

ROOT = Path(__file__).resolve().parents[1]
SEED = 20260912
CASES = 512
REAL_BOX_THRESHOLD = 0.4


def check(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def make_cases() -> list[tuple[str, np.ndarray]]:
    rng = np.random.Generator(np.random.PCG64(SEED))
    rows: list[tuple[str, np.ndarray]] = []
    for index in range(CASES):
        height = 128
        width = 160
        background = np.float32(0.03)
        image = np.full((height, width), background, dtype=np.float32)
        center = (int(rng.integers(35, 125)), int(rng.integers(30, 98)))
        size = (int(rng.integers(24, 72)), int(rng.integers(8, 31)))
        angle = float(rng.uniform(-75.0, 75.0))
        probability = float(rng.uniform(0.35, 0.50))
        points = cv2.boxPoints((center, size, angle)).astype(np.int32)
        cv2.fillConvexPoly(image, points, probability)
        rows.append((f"acceptance-{index:04d}", image))
    return rows


def polygon_iou(left: list[list[float]], right: list[list[float]]) -> float:
    return oracle.polygon_iou(left, right)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    args.work_dir.mkdir(parents=True, exist_ok=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)

    previous_threshold = oracle.BOX_THRESHOLD
    oracle.BOX_THRESHOLD = 0.0
    try:
        cases = make_cases()
        references: dict[str, list[dict]] = {}
        manifest_cases: list[dict] = []
        for index, (name, probability) in enumerate(cases):
            map_file = f"map-{index:04d}.f32"
            probability.astype("<f4", copy=False).tofile(args.work_dir / map_file)
            refs = oracle.reference_boxes(probability, probability.shape[1], probability.shape[0])
            references[name] = refs
            manifest_cases.append({
                "name": name,
                "width": int(probability.shape[1]),
                "height": int(probability.shape[0]),
                "destination_width": int(probability.shape[1]),
                "destination_height": int(probability.shape[0]),
                "map_file": map_file,
            })

        manifest_path = args.work_dir / "manifest.json"
        core_path = args.work_dir / "core.json"
        manifest_path.write_text(json.dumps({"box_threshold": 0.0, "cases": manifest_cases}), encoding="utf-8")
        subprocess.run([
            "dotnet", "run", "--project",
            str(ROOT / "experiments/PhraseLayer.DetectorOutputReplay/PhraseLayer.DetectorOutputReplay.csproj"),
            "-c", "Release", "--", str(manifest_path), str(core_path),
        ], check=True)
        core = json.loads(core_path.read_text(encoding="utf-8"))
        actual_by_name = {row["name"]: row["detections"] for row in core["cases"]}

        compared = 0
        flips = 0
        ref_accept_core_reject = 0
        ref_reject_core_accept = 0
        geometry_mismatch_cases = 0
        maximum_score_error = 0.0
        minimum_iou = 1.0
        examples: list[dict] = []

        for name, _ in cases:
            ref = references[name]
            actual = actual_by_name[name]
            if len(ref) != 1 or len(actual) != 1:
                geometry_mismatch_cases += 1
                if len(examples) < 16:
                    examples.append({"name": name, "reason": "candidate-count", "reference_count": len(ref), "core_count": len(actual)})
                continue
            iou = polygon_iou(ref[0]["points"], actual[0]["points"])
            minimum_iou = min(minimum_iou, iou)
            reference_score = float(ref[0]["score"])
            core_score = float(actual[0]["score"])
            score_error = abs(reference_score - core_score)
            maximum_score_error = max(maximum_score_error, score_error)
            reference_accepts = reference_score >= REAL_BOX_THRESHOLD
            core_accepts = core_score >= REAL_BOX_THRESHOLD
            compared += 1
            if reference_accepts != core_accepts:
                flips += 1
                if reference_accepts:
                    ref_accept_core_reject += 1
                else:
                    ref_reject_core_accept += 1
                if len(examples) < 16:
                    examples.append({
                        "name": name,
                        "reason": "threshold-decision-flip",
                        "reference_score": reference_score,
                        "core_score": core_score,
                        "score_absolute_error": score_error,
                        "iou": iou,
                        "reference_accepts": reference_accepts,
                        "core_accepts": core_accepts,
                    })

        report = {
            "experiment": "ppocr-db-box-score-acceptance-sweep",
            "status": "completed",
            "seed": SEED,
            "cases": CASES,
            "compared_single_candidate_cases": compared,
            "geometry_mismatch_cases": geometry_mismatch_cases,
            "production_box_threshold": REAL_BOX_THRESHOLD,
            "decision_flips": flips,
            "reference_accept_core_reject": ref_accept_core_reject,
            "reference_reject_core_accept": ref_reject_core_accept,
            "maximum_score_absolute_error": maximum_score_error,
            "minimum_iou": minimum_iou,
            "examples": examples,
            "paddle_postprocess_reference_revision": oracle.PADDLE_POSTPROCESS_REFERENCE_REVISION,
            "real_unity_execution_performed": False,
            "gpu_execution_performed": False,
            "quest_execution_performed": False,
            "natural_image_accuracy_measured": False,
            "latency_measured": False,
            "safety_result": "PASS" if flips == 0 and geometry_mismatch_cases == 0 else "FAIL",
        }
        args.report.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        print(json.dumps(report, sort_keys=True))
        return 0 if report["safety_result"] == "PASS" else 1
    finally:
        oracle.BOX_THRESHOLD = previous_threshold


if __name__ == "__main__":
    raise SystemExit(main())
