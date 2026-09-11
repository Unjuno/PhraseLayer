#!/usr/bin/env python3
"""Classify DB topology count mismatches by OpenCV contour hierarchy.

This diagnostic reproduces the topology stress maps, runs production Core, and asks whether every
reference-extra candidate comes from a child contour (a hole/inner boundary) that an 8-connected
component representation cannot express separately. It is diagnostic and never weakens the primary gate.
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


def accepted_contours_with_hierarchy(prediction: np.ndarray) -> list[dict]:
    bitmap = prediction > db.BITMAP_THRESHOLD
    found = cv2.findContours((bitmap * 255).astype(np.uint8), cv2.RETR_TREE, cv2.CHAIN_APPROX_SIMPLE)
    contours = found[0] if len(found) == 2 else found[1]
    hierarchy = found[1] if len(found) == 2 else found[2]
    hierarchy = hierarchy[0] if hierarchy is not None and len(hierarchy) else np.empty((0, 4), dtype=np.int32)
    accepted = []
    for index, contour in enumerate(contours[: min(len(contours), db.MAX_CANDIDATES)]):
        points, short_side = db.mini_box(contour)
        if short_side < db.MIN_SIZE:
            continue
        score = db.box_score_fast(prediction, points.reshape(-1, 2))
        if db.BOX_THRESHOLD > score:
            continue
        expanded = db.unclip_like_paddle(points)
        if len(expanded) > 1:
            continue
        expanded_array = np.asarray(expanded).reshape(-1, 1, 2)
        if len(expanded_array) == 0:
            continue
        _, final_short = db.mini_box(expanded_array)
        if final_short < db.MIN_SIZE + 2.0:
            continue
        parent = int(hierarchy[index][3]) if index < len(hierarchy) else -1
        child = int(hierarchy[index][2]) if index < len(hierarchy) else -1
        accepted.append({
            "contour_index": index,
            "parent_index": parent,
            "child_index": child,
            "is_child_contour": parent >= 0,
            "score": float(score),
            "point_count": int(len(contour)),
        })
    return accepted


def component_count(prediction: np.ndarray) -> int:
    mask = (prediction > db.BITMAP_THRESHOLD).astype(np.uint8)
    count, _ = cv2.connectedComponents(mask, connectivity=8)
    return int(count - 1)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    args.work_dir.mkdir(parents=True, exist_ok=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)

    cases = stress.deterministic_cases() + stress.random_cases()
    manifest_cases = []
    hierarchy_by_name = {}
    reference_counts = {}
    for index, (name, probability) in enumerate(cases):
        map_file = f"map-{index:04d}.f32"
        probability.astype("<f4", copy=False).tofile(args.work_dir / map_file)
        accepted = accepted_contours_with_hierarchy(probability)
        hierarchy_by_name[name] = {
            "foreground_connected_components": component_count(probability),
            "accepted_contours": accepted,
            "accepted_child_contours": sum(1 for row in accepted if row["is_child_contour"]),
        }
        reference_counts[name] = len(db.reference_boxes(probability, probability.shape[1], probability.shape[0]))
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
    manifest.write_text(json.dumps({"cases": manifest_cases}), encoding="utf-8")
    subprocess.run([
        "dotnet", "run", "--project",
        str(ROOT / "experiments/PhraseLayer.DetectorOutputReplay/PhraseLayer.DetectorOutputReplay.csproj"),
        "-c", "Release", "--", str(manifest), str(core_path),
    ], check=True)
    core = json.loads(core_path.read_text(encoding="utf-8"))
    core_counts = {row["name"]: len(row["detections"]) for row in core["cases"]}

    mismatches = []
    reference_extra = 0
    core_extra = 0
    all_reference_extra_explained_by_child_contours = True
    for name, _ in cases:
        reference_count = reference_counts[name]
        core_count = core_counts[name]
        if reference_count == core_count:
            continue
        topology = hierarchy_by_name[name]
        delta = reference_count - core_count
        if delta > 0:
            reference_extra += delta
            explained = topology["accepted_child_contours"] >= delta
            all_reference_extra_explained_by_child_contours &= explained
        else:
            core_extra += -delta
            explained = False
            all_reference_extra_explained_by_child_contours = False
        mismatches.append({
            "name": name,
            "reference_count": reference_count,
            "core_count": core_count,
            "delta_reference_minus_core": delta,
            "foreground_connected_components": topology["foreground_connected_components"],
            "accepted_contour_count": len(topology["accepted_contours"]),
            "accepted_child_contours": topology["accepted_child_contours"],
            "reference_extra_explained_by_child_contours": explained,
            "accepted_contours": topology["accepted_contours"],
        })

    report = {
        "experiment": "ppocr-db-topology-hierarchy-diagnostic",
        "status": "completed",
        "total_cases": len(cases),
        "count_mismatch_cases": len(mismatches),
        "reference_extra_candidates": reference_extra,
        "core_extra_candidates": core_extra,
        "all_reference_extra_explained_by_accepted_child_contours": all_reference_extra_explained_by_child_contours,
        "mismatches": mismatches,
        "paddle_postprocess_reference_revision": db.PADDLE_POSTPROCESS_REFERENCE_REVISION,
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
