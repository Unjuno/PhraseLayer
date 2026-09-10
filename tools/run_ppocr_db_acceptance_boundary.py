#!/usr/bin/env python3
"""Stress PP-OCR DB box-score acceptance around box_thresh=0.4.

This isolates one question from the broader detector geometry differential: can the dependency-free
Core raster score and PaddleOCR/OpenCV fillPoly score make different accept/reject decisions for the
same thresholded component? Inputs are deterministic synthetic float32 probability maps. No model,
image, camera, Unity or Quest execution is involved.
"""
from __future__ import annotations

import argparse
import importlib.metadata
import json
from pathlib import Path
import platform
import subprocess

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
BITMAP_THRESHOLD = np.float32(0.2)
BOX_THRESHOLD = 0.4
MIN_SIZE = 3.0
SEED = 20260911


def order_box(points: np.ndarray) -> np.ndarray:
    ordered = sorted(list(points), key=lambda point: point[0])
    if ordered[1][1] > ordered[0][1]:
        index_1, index_4 = 0, 1
    else:
        index_1, index_4 = 1, 0
    if ordered[3][1] > ordered[2][1]:
        index_2, index_3 = 2, 3
    else:
        index_2, index_3 = 3, 2
    return np.asarray([ordered[index_1], ordered[index_2], ordered[index_3], ordered[index_4]], dtype=np.float32)


def box_score_fast(prediction: np.ndarray, box: np.ndarray) -> float:
    height, width = prediction.shape
    local = box.copy()
    xmin = int(np.clip(np.floor(local[:, 0].min()).astype("int32"), 0, width - 1))
    xmax = int(np.clip(np.ceil(local[:, 0].max()).astype("int32"), 0, width - 1))
    ymin = int(np.clip(np.floor(local[:, 1].min()).astype("int32"), 0, height - 1))
    ymax = int(np.clip(np.ceil(local[:, 1].max()).astype("int32"), 0, height - 1))
    mask = np.zeros((ymax - ymin + 1, xmax - xmin + 1), dtype=np.uint8)
    local[:, 0] -= xmin
    local[:, 1] -= ymin
    cv2.fillPoly(mask, local.reshape(1, -1, 2).astype("int32"), 1)
    return float(cv2.mean(prediction[ymin : ymax + 1, xmin : xmax + 1], mask)[0])


def paddle_candidate_score(prediction: np.ndarray) -> tuple[float | None, np.ndarray | None]:
    bitmap = prediction > BITMAP_THRESHOLD
    contours, _ = cv2.findContours((bitmap * 255).astype(np.uint8), cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
    if len(contours) != 1:
        return None, None
    rectangle = cv2.minAreaRect(contours[0])
    if min(rectangle[1]) < MIN_SIZE:
        return None, None
    box = order_box(cv2.boxPoints(rectangle))
    return box_score_fast(prediction, box), box


def build_cases(work_dir: Path) -> tuple[list[dict], dict[str, dict]]:
    manifest_cases: list[dict] = []
    expected: dict[str, dict] = {}
    case_index = 0
    angles = [-75.0, -60.0, -45.0, -30.0, -15.0, 0.0, 15.0, 30.0, 45.0, 60.0, 75.0]
    sizes = [(18, 6), (28, 9), (44, 14), (64, 20)]
    backgrounds = [0.01, 0.03, 0.10, 0.19]
    # Dense around the acceptance gate, with wider shoulders to prove both sides agree away from it.
    signals = np.concatenate([
        np.linspace(0.405, 0.455, 11),
        np.linspace(0.46, 0.60, 15),
        np.asarray([0.65, 0.75, 0.90], dtype=np.float64),
    ])

    for width, height in sizes:
        for angle in angles:
            for background in backgrounds:
                for signal in signals:
                    probability = np.full((128, 128), np.float32(background), dtype=np.float32)
                    polygon = cv2.boxPoints(((64.0, 64.0), (float(width), float(height)), angle)).astype(np.int32)
                    cv2.fillConvexPoly(probability, polygon, float(signal))
                    reference_score, reference_box = paddle_candidate_score(probability)
                    if reference_score is None:
                        continue
                    name = f"w{width}-h{height}-a{angle:+05.1f}-b{background:.2f}-s{signal:.3f}"
                    map_file = f"map-{case_index:05d}.f32"
                    probability.astype("<f4", copy=False).tofile(work_dir / map_file)
                    manifest_cases.append({
                        "name": name,
                        "width": 128,
                        "height": 128,
                        "destination_width": 128,
                        "destination_height": 128,
                        "map_file": map_file,
                    })
                    expected[name] = {
                        "reference_score": reference_score,
                        "reference_accepts": reference_score >= BOX_THRESHOLD,
                        "width": width,
                        "height": height,
                        "angle": angle,
                        "background": background,
                        "signal": float(signal),
                        "reference_box": reference_box.astype(float).tolist(),
                    }
                    case_index += 1
    return manifest_cases, expected


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    args.work_dir.mkdir(parents=True, exist_ok=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)

    manifest_cases, expected = build_cases(args.work_dir)
    manifest_path = args.work_dir / "manifest.json"
    core_path = args.work_dir / "core.json"
    manifest_path.write_text(json.dumps({"cases": manifest_cases}), encoding="utf-8")
    subprocess.run([
        "dotnet", "run", "--project",
        str(ROOT / "experiments/PhraseLayer.DetectorOutputReplay/PhraseLayer.DetectorOutputReplay.csproj"),
        "-c", "Release", "--", str(manifest_path), str(core_path)
    ], check=True)
    core = json.loads(core_path.read_text(encoding="utf-8"))
    actual = {row["name"]: row["detections"] for row in core["cases"]}

    flips = []
    accepted_by_both = 0
    rejected_by_both = 0
    for name, reference in expected.items():
        core_count = len(actual[name])
        core_accepts = core_count > 0
        if core_count > 1:
            flips.append({**reference, "name": name, "reason": "core_returned_multiple_boxes", "core_count": core_count})
            continue
        if core_accepts != reference["reference_accepts"]:
            flips.append({**reference, "name": name, "reason": "acceptance_flip", "core_count": core_count,
                          "core_score": None if not core_accepts else actual[name][0]["score"]})
        elif core_accepts:
            accepted_by_both += 1
        else:
            rejected_by_both += 1

    nearest_flips = sorted(flips, key=lambda row: abs(row["reference_score"] - BOX_THRESHOLD))[:32]
    report = {
        "experiment": "ppocr-db-box-score-acceptance-boundary",
        "status": "completed",
        "safety_result": "PASS" if not flips else "FAIL",
        "seed": SEED,
        "total_cases": len(expected),
        "acceptance_flip_cases": len(flips),
        "accepted_by_both": accepted_by_both,
        "rejected_by_both": rejected_by_both,
        "box_threshold": BOX_THRESHOLD,
        "bitmap_threshold_float32": float(BITMAP_THRESHOLD),
        "nearest_flips": nearest_flips,
        "opencv_version": cv2.__version__,
        "numpy_version": np.__version__,
        "framework": platform.python_version(),
        "real_model_execution_performed": False,
        "real_unity_execution_performed": False,
        "gpu_execution_performed": False,
        "quest_execution_performed": False,
        "latency_measured": False,
    }
    args.report.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(report, sort_keys=True))
    return 0 if not flips else 1


if __name__ == "__main__":
    raise SystemExit(main())
