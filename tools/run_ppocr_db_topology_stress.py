#!/usr/bin/env python3
"""Stress DB contour topology where connected components and RETR_LIST can differ.

The oracle is the pinned Paddle/OpenCV/pyclipper implementation from run_ppocr_detector_db_differential.
Production Core is executed through DetectorOutputReplay. The test focuses on holes, touching/overlapping
rectangles, L/T/cross shapes, and near-threshold component mixtures. It does not measure OCR accuracy.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess

import cv2
import numpy as np

import run_ppocr_detector_db_differential as db

ROOT = Path(__file__).resolve().parents[1]
SEED = 20260912
RANDOM_CASES = 768


def rect(image: np.ndarray, x0: int, y0: int, x1: int, y1: int, value: float) -> None:
    image[max(0, y0):min(image.shape[0], y1), max(0, x0):min(image.shape[1], x1)] = np.float32(value)


def deterministic_cases() -> list[tuple[str, np.ndarray]]:
    cases: list[tuple[str, np.ndarray]] = []
    for hole_scale in (0.15, 0.3, 0.5, 0.7):
        for ring_value in (0.42, 0.55, 0.8, 0.95):
            image = np.full((128, 160), 0.03, np.float32)
            rect(image, 25, 25, 135, 103, ring_value)
            hw = int(110 * hole_scale / 2)
            hh = int(78 * hole_scale / 2)
            rect(image, 80 - hw, 64 - hh, 80 + hw, 64 + hh, 0.03)
            cases.append((f"ring-s{hole_scale:.2f}-p{ring_value:.2f}", image))

    # One connected component with strongly non-convex geometry.
    shapes = [
        ("L", [(24, 22, 48, 106), (24, 82, 128, 106)]),
        ("T", [(24, 22, 136, 48), (68, 22, 92, 108)]),
        ("cross", [(62, 18, 98, 112), (20, 50, 140, 82)]),
        ("stair", [(20, 20, 70, 45), (50, 40, 105, 70), (85, 65, 140, 100)]),
    ]
    for name, rectangles in shapes:
        for probability in (0.39, 0.401, 0.45, 0.7, 0.95):
            image = np.full((128, 160), 0.03, np.float32)
            for bounds in rectangles:
                rect(image, *bounds, probability)
            cases.append((f"{name}-p{probability:.3f}", image))
    return cases


def random_cases() -> list[tuple[str, np.ndarray]]:
    rng = np.random.Generator(np.random.PCG64(SEED))
    result: list[tuple[str, np.ndarray]] = []
    for index in range(RANDOM_CASES):
        image = np.full((128, 160), np.float32(rng.uniform(0.0, 0.08)), np.float32)
        count = int(rng.integers(2, 5))
        anchor_x = int(rng.integers(35, 125))
        anchor_y = int(rng.integers(30, 100))
        for primitive in range(count):
            # Deliberately cluster primitives so many of them touch or overlap into one non-convex component.
            center = (anchor_x + int(rng.integers(-28, 29)), anchor_y + int(rng.integers(-24, 25)))
            size = (int(rng.integers(18, 68)), int(rng.integers(8, 32)))
            angle = float(rng.uniform(-80, 80))
            points = cv2.boxPoints((center, size, angle)).astype(np.int32)
            probability = float(rng.uniform(0.25, 0.95))
            cv2.fillConvexPoly(image, points, probability)

        if index % 4 == 0:
            # Cut a low-probability hole through a foreground component when possible.
            cx = anchor_x + int(rng.integers(-10, 11))
            cy = anchor_y + int(rng.integers(-10, 11))
            hw = int(rng.integers(3, 18)); hh = int(rng.integers(3, 14))
            rect(image, cx - hw, cy - hh, cx + hw, cy + hh, float(rng.uniform(0.0, 0.18)))
        result.append((f"random-topology-{index:04d}", image))
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    args.work_dir.mkdir(parents=True, exist_ok=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)

    cases = deterministic_cases() + random_cases()
    references: dict[str, list[dict]] = {}
    manifest_cases = []
    for index, (name, probability) in enumerate(cases):
        references[name] = db.reference_boxes(probability, probability.shape[1], probability.shape[0])
        map_file = f"map-{index:04d}.f32"
        probability.astype("<f4", copy=False).tofile(args.work_dir / map_file)
        manifest_cases.append({"name": name, "width": int(probability.shape[1]), "height": int(probability.shape[0]),
                               "destination_width": int(probability.shape[1]), "destination_height": int(probability.shape[0]),
                               "map_file": map_file})

    manifest = args.work_dir / "manifest.json"
    core_path = args.work_dir / "core.json"
    manifest.write_text(json.dumps({"cases": manifest_cases}), encoding="utf-8")
    subprocess.run(["dotnet", "run", "--project",
                    str(ROOT / "experiments/PhraseLayer.DetectorOutputReplay/PhraseLayer.DetectorOutputReplay.csproj"),
                    "-c", "Release", "--", str(manifest), str(core_path)], check=True)
    core = json.loads(core_path.read_text(encoding="utf-8"))
    actual_by_name = {row["name"]: row["detections"] for row in core["cases"]}

    count_mismatches = 0
    score_decision_mismatches = 0
    low_iou_matches = 0
    compared_matches = 0
    minimum_iou = 1.0
    maximum_score_error = 0.0
    examples = []
    for name, _ in cases:
        ref = references[name]
        actual = actual_by_name[name]
        if len(ref) != len(actual):
            count_mismatches += 1
            if len(examples) < 24:
                examples.append({"name": name, "reason": "count", "reference_count": len(ref), "core_count": len(actual)})
        unmatched = set(range(len(actual)))
        for expected in ref:
            if not unmatched:
                break
            ranked = sorted(((db.polygon_iou(expected["points"], actual[i]["points"]), i) for i in unmatched), reverse=True)
            iou, selected = ranked[0]
            unmatched.remove(selected)
            observed = actual[selected]
            score_error = abs(float(expected["score"]) - float(observed["score"]))
            maximum_score_error = max(maximum_score_error, score_error)
            minimum_iou = min(minimum_iou, iou)
            compared_matches += 1
            if iou < 0.85:
                low_iou_matches += 1
                if len(examples) < 24:
                    examples.append({"name": name, "reason": "geometry", "iou": iou,
                                     "reference_score": expected["score"], "core_score": observed["score"]})
            if (float(expected["score"]) >= db.BOX_THRESHOLD) != (float(observed["score"]) >= db.BOX_THRESHOLD):
                score_decision_mismatches += 1

    # Count equality at the real production threshold is the primary safety property. A very low matched IoU is
    # also treated as a failure because it can materially alter recognition crops. Moderate 1-2 px unclip drift is
    # reported but does not by itself fail this topology experiment.
    safety = count_mismatches == 0 and score_decision_mismatches == 0 and low_iou_matches == 0
    report = {
        "experiment": "ppocr-db-topology-stress",
        "status": "completed",
        "safety_result": "PASS" if safety else "FAIL",
        "seed": SEED,
        "deterministic_cases": len(deterministic_cases()),
        "random_cases": RANDOM_CASES,
        "total_cases": len(cases),
        "compared_matches": compared_matches,
        "count_mismatches": count_mismatches,
        "score_decision_mismatches": score_decision_mismatches,
        "matches_below_iou_0_85": low_iou_matches,
        "minimum_iou": minimum_iou,
        "maximum_score_absolute_error": maximum_score_error,
        "examples": examples,
        "paddle_postprocess_reference_revision": db.PADDLE_POSTPROCESS_REFERENCE_REVISION,
        "real_model_execution_performed": False,
        "real_unity_execution_performed": False,
        "gpu_execution_performed": False,
        "quest_execution_performed": False,
        "natural_image_accuracy_measured": False,
        "latency_measured": False,
    }
    args.report.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(report, sort_keys=True))
    return 0 if safety else 1


if __name__ == "__main__":
    raise SystemExit(main())
