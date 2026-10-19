#!/usr/bin/env python3
"""Reproduce complex non-convex DB candidates that flip around the 0.4 box threshold.

The maps come from the deterministic topology stress seed. Both Paddle/OpenCV and production Core are
replayed with box_threshold=0 so their pre-filter candidate scores can be observed. The real 0.4 product
threshold is then applied offline. This diagnostic distinguishes threshold-score drift from child-contour
(topology) differences without changing production behavior.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess

import cv2
import numpy as np

import run_ppocr_detector_db_differential as db
import run_ppocr_db_topology_stress as stress

ROOT = Path(__file__).resolve().parents[1]
TARGETS = ("random-topology-0061", "random-topology-0103")
PRODUCTION_THRESHOLD = 0.4


def reference_outer_candidates_at_zero(prediction: np.ndarray) -> list[dict]:
    bitmap = prediction > db.BITMAP_THRESHOLD
    found = cv2.findContours((bitmap * 255).astype(np.uint8), cv2.RETR_TREE, cv2.CHAIN_APPROX_SIMPLE)
    contours = found[0] if len(found) == 2 else found[1]
    hierarchy = found[1] if len(found) == 2 else found[2]
    hierarchy = hierarchy[0] if hierarchy is not None and len(hierarchy) else np.empty((0, 4), dtype=np.int32)
    height, width = bitmap.shape
    rows: list[dict] = []
    for index, contour in enumerate(contours[: min(len(contours), db.MAX_CANDIDATES)]):
        if index < len(hierarchy) and int(hierarchy[index][3]) >= 0:
            continue
        points, short_side = db.mini_box(contour)
        if short_side < db.MIN_SIZE:
            continue
        score = db.box_score_fast(prediction, points.reshape(-1, 2))
        expanded = db.unclip_like_paddle(points)
        if len(expanded) != 1:
            continue
        expanded_array = np.asarray(expanded).reshape(-1, 1, 2)
        if len(expanded_array) == 0:
            continue
        final_box, final_short = db.mini_box(expanded_array)
        if final_short < db.MIN_SIZE + 2.0:
            continue
        final_box = np.asarray(final_box, dtype=np.float32)
        final_box[:, 0] = np.clip(np.round(final_box[:, 0] / width * width), 0, width)
        final_box[:, 1] = np.clip(np.round(final_box[:, 1] / height * height), 0, height)
        rows.append({
            "contour_index": index,
            "score": float(score),
            "points": final_box.astype("int32").astype(float).tolist(),
        })
    return rows


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    args.work_dir.mkdir(parents=True, exist_ok=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)

    case_map = dict(stress.random_cases())
    manifest_cases = []
    references: dict[str, list[dict]] = {}
    for index, name in enumerate(TARGETS):
        probability = case_map[name]
        references[name] = reference_outer_candidates_at_zero(probability)
        map_file = f"map-{index:02d}.f32"
        probability.astype("<f4", copy=False).tofile(args.work_dir / map_file)
        manifest_cases.append({
            "name": name,
            "width": int(probability.shape[1]),
            "height": int(probability.shape[0]),
            "destination_width": int(probability.shape[1]),
            "destination_height": int(probability.shape[0]),
            "map_file": map_file,
        })

    manifest = args.work_dir / "manifest.json"
    core_path = args.work_dir / "core.json"
    manifest.write_text(json.dumps({"box_threshold": 0.0, "cases": manifest_cases}), encoding="utf-8")
    subprocess.run([
        "dotnet", "run", "--project",
        str(ROOT / "experiments/PhraseLayer.DetectorOutputReplay/PhraseLayer.DetectorOutputReplay.csproj"),
        "-c", "Release", "--", str(manifest), str(core_path),
    ], check=True)
    core = json.loads(core_path.read_text(encoding="utf-8"))
    core_by_name = {row["name"]: row["detections"] for row in core["cases"]}

    rows = []
    flips = 0
    for name in TARGETS:
        refs = references[name]
        actual = core_by_name[name]
        # These reproductions have one foreground connected component. Compare the outer Paddle contour
        # to the Core component candidate by maximum final-quad IoU.
        if not refs or not actual:
            rows.append({
                "name": name,
                "reference_candidates_at_zero": len(refs),
                "core_candidates_at_zero": len(actual),
                "comparison_available": False,
            })
            flips += 1
            continue
        ref = refs[0]
        ranked = sorted(((db.polygon_iou(ref["points"], item["points"]), item) for item in actual), reverse=True, key=lambda row: row[0])
        iou, observed = ranked[0]
        ref_score = float(ref["score"])
        core_score = float(observed["score"])
        ref_accept = ref_score >= PRODUCTION_THRESHOLD
        core_accept = core_score >= PRODUCTION_THRESHOLD
        flip = ref_accept != core_accept
        flips += int(flip)
        rows.append({
            "name": name,
            "comparison_available": True,
            "reference_candidates_at_zero": len(refs),
            "core_candidates_at_zero": len(actual),
            "reference_outer_score": ref_score,
            "core_component_score": core_score,
            "score_absolute_delta": abs(ref_score - core_score),
            "reference_margin_from_0_4": ref_score - PRODUCTION_THRESHOLD,
            "core_margin_from_0_4": core_score - PRODUCTION_THRESHOLD,
            "reference_accepts_at_0_4": ref_accept,
            "core_accepts_at_0_4": core_accept,
            "decision_flip": flip,
            "final_quad_iou": iou,
            "reference_points": ref["points"],
            "core_points": observed["points"],
        })

    report = {
        "experiment": "ppocr-db-near-threshold-complex-component-diagnostic",
        "status": "completed",
        "seed": stress.SEED,
        "targets": list(TARGETS),
        "production_box_threshold": PRODUCTION_THRESHOLD,
        "decision_flips_reproduced": flips,
        "rows": rows,
        "production_change_applied": False,
        "real_model_execution_performed": False,
        "real_unity_execution_performed": False,
        "quest_execution_performed": False,
    }
    args.report.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(report, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
