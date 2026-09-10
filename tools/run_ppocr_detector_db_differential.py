#!/usr/bin/env python3
"""Compare production Core DB quad postprocessing with an upstream-faithful PaddleOCR oracle.

The oracle mirrors PaddleOCR ppocr/postprocess/db_postprocess.py as last changed by
PaddlePaddle/PaddleOCR commit de12ece0aacb60a14268cdce94609b515d117e54.
The audit uses deterministic synthetic maps and the exact pinned PP-OCRv6 tiny detector executed
with ONNX Runtime CPUExecutionProvider. Model bytes, images and probability maps stay local.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import math
from pathlib import Path
import platform
import subprocess

import cv2
import numpy as np
import onnx
import onnxruntime as ort
import pyclipper
from shapely.geometry import Polygon

ROOT = Path(__file__).resolve().parents[1]
PADDLE_POSTPROCESS_REFERENCE_REVISION = "de12ece0aacb60a14268cdce94609b515d117e54"
BITMAP_THRESHOLD = 0.2
BOX_THRESHOLD = 0.4
MAX_CANDIDATES = 3000
UNCLIP_RATIO = 1.4
MIN_SIZE = 3.0
IOU_ACCEPTANCE = 0.90
CENTER_ACCEPTANCE_PIXELS = 2.0
SCORE_ACCEPTANCE = 0.03


def check(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def model_candidate() -> dict:
    lock = json.loads((ROOT / "models/models.lock.json").read_text(encoding="utf-8"))
    return next(item for item in lock["candidates"] if item["id"] == "pp-ocrv6-tiny-det")


def order_box(points: np.ndarray) -> np.ndarray:
    ordered = sorted(list(points), key=lambda point: point[0])
    index_1, index_2, index_3, index_4 = 0, 1, 2, 3
    if ordered[1][1] > ordered[0][1]:
        index_1, index_4 = 0, 1
    else:
        index_1, index_4 = 1, 0
    if ordered[3][1] > ordered[2][1]:
        index_2, index_3 = 2, 3
    else:
        index_2, index_3 = 3, 2
    return np.asarray([ordered[index_1], ordered[index_2], ordered[index_3], ordered[index_4]], dtype=np.float32)


def mini_box(contour: np.ndarray) -> tuple[np.ndarray, float]:
    bounding_box = cv2.minAreaRect(contour)
    return order_box(cv2.boxPoints(bounding_box)), float(min(bounding_box[1]))


def box_score_fast(prediction: np.ndarray, box: np.ndarray) -> float:
    height, width = prediction.shape[:2]
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


def unclip_like_paddle(box: np.ndarray) -> list[list[list[int]]]:
    # PaddleOCR computes distance with Shapely, then passes the floating min-area box directly to
    # pyclipper. pyclipper converts coordinates to its integer geometry; pre-rounding here would alter
    # that conversion and is deliberately forbidden by this oracle.
    polygon = Polygon(box)
    if polygon.length <= 0:
        return []
    distance = polygon.area * UNCLIP_RATIO / polygon.length
    offset = pyclipper.PyclipperOffset()
    offset.AddPath(box.tolist(), pyclipper.JT_ROUND, pyclipper.ET_CLOSEDPOLYGON)
    return offset.Execute(distance)


def reference_boxes(prediction: np.ndarray, destination_width: int, destination_height: int) -> list[dict]:
    bitmap = prediction > BITMAP_THRESHOLD
    found = cv2.findContours((bitmap * 255).astype(np.uint8), cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
    contours = found[0] if len(found) == 2 else found[1]
    height, width = bitmap.shape
    boxes: list[dict] = []
    for contour in contours[: min(len(contours), MAX_CANDIDATES)]:
        points, short_side = mini_box(contour)
        if short_side < MIN_SIZE:
            continue
        score = box_score_fast(prediction, points.reshape(-1, 2))
        if BOX_THRESHOLD > score:
            continue
        expanded = unclip_like_paddle(points)
        if len(expanded) > 1:
            continue
        expanded_array = np.asarray(expanded).reshape(-1, 1, 2)
        if len(expanded_array) == 0:
            continue
        final_box, final_short_side = mini_box(expanded_array)
        if final_short_side < MIN_SIZE + 2.0:
            continue
        final_box = np.asarray(final_box)
        final_box[:, 0] = np.clip(np.round(final_box[:, 0] / width * destination_width), 0, destination_width)
        final_box[:, 1] = np.clip(np.round(final_box[:, 1] / height * destination_height), 0, destination_height)
        boxes.append({"score": score, "points": final_box.astype("int32").astype(float).tolist()})
    return boxes


def synthetic_maps() -> list[tuple[str, np.ndarray]]:
    cases: list[tuple[str, np.ndarray]] = []
    def blank(height: int = 96, width: int = 128) -> np.ndarray:
        return np.full((height, width), 0.03, dtype=np.float32)

    axis = blank(); axis[28:52, 24:92] = 0.93; cases.append(("synthetic-axis-rectangle", axis))
    pair = blank(); pair[16:36, 12:50] = 0.91; pair[55:79, 68:116] = 0.86; cases.append(("synthetic-two-components", pair))
    rotated = blank(); cv2.fillConvexPoly(rotated, np.asarray([[25, 54], [39, 20], [103, 42], [89, 76]], np.int32), 0.89); cases.append(("synthetic-rotated-quad", rotated))
    edge = blank(); edge[0:24, 0:72] = 0.88; cases.append(("synthetic-edge-clipped", edge))
    threshold = blank(); threshold[28:52, 24:92] = np.float32(BITMAP_THRESHOLD); threshold[31:49, 27:89] = 0.75; cases.append(("synthetic-threshold-equality-background", threshold))
    ring = blank(); ring[18:78, 24:104] = 0.90; ring[34:62, 44:84] = 0.03; cases.append(("synthetic-hole-contour", ring))

    rng = np.random.Generator(np.random.PCG64(20260911))
    for index in range(24):
        image = blank(128, 160)
        for _ in range(1 + (index % 3)):
            center = (int(rng.integers(30, 130)), int(rng.integers(25, 103)))
            size = (int(rng.integers(20, 60)), int(rng.integers(9, 28)))
            angle = float(rng.uniform(-65, 65))
            points = cv2.boxPoints((center, size, angle)).astype(np.int32)
            cv2.fillConvexPoly(image, points, float(rng.uniform(0.55, 0.98)))
        cases.append((f"synthetic-seeded-{index:02d}", image))
    return cases


def render_detector_fixture(lines: list[str], width: int = 736, height: int = 736) -> np.ndarray:
    image = np.full((height, width, 3), 255, dtype=np.uint8)
    y = 180
    for line in lines:
        scale = 2.2 if len(line) < 12 else 1.5
        thickness = 5
        (text_width, text_height), _ = cv2.getTextSize(line, cv2.FONT_HERSHEY_SIMPLEX, scale, thickness)
        x = max(12, (width - text_width) // 2)
        cv2.putText(image, line, (x, y), cv2.FONT_HERSHEY_SIMPLEX, scale, (0, 0, 0), thickness, cv2.LINE_AA)
        y += text_height + 120
    return image


def detector_input(bgr: np.ndarray) -> np.ndarray:
    values = bgr.astype(np.float32) / 255.0
    means = np.asarray([0.485, 0.456, 0.406], dtype=np.float32)
    standard_deviations = np.asarray([0.229, 0.224, 0.225], dtype=np.float32)
    return np.transpose((values - means) / standard_deviations, (2, 0, 1))[None].astype(np.float32)


def actual_model_maps(model_path: Path) -> tuple[list[tuple[str, np.ndarray]], dict]:
    candidate = model_candidate()
    raw = model_path.read_bytes()
    check(len(raw) == candidate["artifact_size_bytes"], "Detector model size differs from lock")
    check(hashlib.sha256(raw).hexdigest() == candidate["artifact_sha256"], "Detector model SHA-256 differs from lock")
    model = onnx.load_model_from_string(raw); onnx.checker.check_model(model)
    options = ort.SessionOptions(); options.intra_op_num_threads = 1; options.inter_op_num_threads = 1
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    runtime = ort.InferenceSession(raw, options, providers=["CPUExecutionProvider"])
    inputs = runtime.get_inputs(); check(len(inputs) == 1 and inputs[0].type == "tensor(float)", "Detector must have one float input")
    fixtures = [("model-keep-off", ["KEEP OFF"]), ("model-emergency-exit", ["EMERGENCY EXIT"]),
                ("model-two-lines", ["KEEP OFF", "EMERGENCY EXIT"]), ("model-mixed-short", ["ROOM 204", "EXIT"])]
    maps: list[tuple[str, np.ndarray]] = []
    for name, lines in fixtures:
        output = np.asarray(runtime.run(None, {inputs[0].name: detector_input(render_detector_fixture(lines))})[0], dtype=np.float32)
        if output.ndim == 4:
            check(output.shape[0] == 1 and output.shape[1] == 1, "Rank-4 detector output must be [1,1,H,W]"); probability = output[0, 0]
        elif output.ndim == 3:
            check(output.shape[0] == 1, "Rank-3 detector output must be [1,H,W]"); probability = output[0]
        elif output.ndim == 2:
            probability = output
        else:
            raise ValueError(f"Unsupported detector output rank: {output.shape}")
        check(np.isfinite(probability).all() and np.logical_and(probability >= 0, probability <= 1).all(), "Detector output is not a finite probability map")
        maps.append((name, probability.copy()))
    return maps, {"model_revision": candidate["revision"], "model_sha256": hashlib.sha256(raw).hexdigest(),
                  "provider": runtime.get_providers(), "input_shape": inputs[0].shape, "onnx_ir_version": model.ir_version,
                  "onnx_opsets": {item.domain or "ai.onnx": item.version for item in model.opset_import}}


def polygon_iou(left: list[list[float]], right: list[list[float]]) -> float:
    a = cv2.convexHull(np.asarray(left, dtype=np.float32)).reshape((-1, 2))
    b = cv2.convexHull(np.asarray(right, dtype=np.float32)).reshape((-1, 2))
    area_a = abs(float(cv2.contourArea(a))); area_b = abs(float(cv2.contourArea(b)))
    intersection, _ = cv2.intersectConvexConvex(a, b)
    union = area_a + area_b - float(intersection)
    return 1.0 if union <= 0 else float(intersection) / union


def center(points: list[list[float]]) -> np.ndarray:
    return np.asarray(points, dtype=np.float64).mean(axis=0)


def compare_case(reference: list[dict], actual: list[dict]) -> dict:
    unmatched = set(range(len(actual))); matches = []
    for expected_index, expected in enumerate(reference):
        if not unmatched:
            break
        ranked = sorted(((polygon_iou(expected["points"], actual[index]["points"]), index) for index in unmatched), reverse=True)
        iou, actual_index = ranked[0]; unmatched.remove(actual_index); observed = actual[actual_index]
        matches.append({"reference_index": expected_index, "core_index": actual_index, "iou": iou,
                        "center_error_pixels": float(np.linalg.norm(center(expected["points"]) - center(observed["points"]))),
                        "reference_score": float(expected["score"]), "core_score": float(observed["score"]),
                        "score_absolute_error": abs(float(expected["score"]) - float(observed["score"])),
                        "reference_points": expected["points"], "core_points": observed["points"]})
    count_equal = len(reference) == len(actual)
    minimum_iou = min((row["iou"] for row in matches), default=(1.0 if not reference and not actual else 0.0))
    maximum_center = max((row["center_error_pixels"] for row in matches), default=0.0)
    maximum_score = max((row["score_absolute_error"] for row in matches), default=0.0)
    passed = count_equal and len(matches) == len(reference) and minimum_iou >= IOU_ACCEPTANCE and maximum_center <= CENTER_ACCEPTANCE_PIXELS and maximum_score <= SCORE_ACCEPTANCE
    return {"reference_count": len(reference), "core_count": len(actual), "count_equal": count_equal,
            "minimum_iou": minimum_iou, "maximum_center_error_pixels": maximum_center,
            "maximum_score_absolute_error": maximum_score, "passed": passed, "matches": matches}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", type=Path, required=True); parser.add_argument("--work-dir", type=Path, required=True); parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args(); args.work_dir.mkdir(parents=True, exist_ok=True); args.report.parent.mkdir(parents=True, exist_ok=True)
    report = {"experiment": "ppocr-detector-db-upstream-production-core-differential", "status": "uncertain", "seed": 20260911,
              "paddle_postprocess_reference_revision": PADDLE_POSTPROCESS_REFERENCE_REVISION,
              "versions": {name: importlib.metadata.version(name) for name in ("numpy", "onnx", "onnxruntime", "opencv-python-headless", "pyclipper", "shapely")},
              "python": platform.python_version(), "architecture": platform.machine(), "real_unity_execution_performed": False,
              "gpu_execution_performed": False, "quest_execution_performed": False, "natural_image_accuracy_measured": False, "latency_measured": False}
    try:
        model_maps, model_metadata = actual_model_maps(args.model); all_maps = synthetic_maps() + model_maps
        manifest_cases = []; references: dict[str, list[dict]] = {}
        for index, (name, probability) in enumerate(all_maps):
            map_file = f"map-{index:03d}.f32"; probability.astype("<f4", copy=False).tofile(args.work_dir / map_file)
            references[name] = reference_boxes(probability, probability.shape[1], probability.shape[0])
            manifest_cases.append({"name": name, "width": int(probability.shape[1]), "height": int(probability.shape[0]),
                                   "destination_width": int(probability.shape[1]), "destination_height": int(probability.shape[0]), "map_file": map_file})
        manifest_path = args.work_dir / "manifest.json"; core_path = args.work_dir / "core.json"
        manifest_path.write_text(json.dumps({"cases": manifest_cases}), encoding="utf-8")
        subprocess.run(["dotnet", "run", "--project", str(ROOT / "experiments/PhraseLayer.DetectorOutputReplay/PhraseLayer.DetectorOutputReplay.csproj"), "-c", "Release", "--", str(manifest_path), str(core_path)], check=True)
        core = json.loads(core_path.read_text(encoding="utf-8")); actual_by_name = {row["name"]: row["detections"] for row in core["cases"]}
        rows = []
        for name, _ in all_maps:
            result = compare_case(references[name], actual_by_name[name]); result["name"] = name; result["actual_model_output"] = name.startswith("model-"); rows.append(result)
        failures = [row for row in rows if not row["passed"]]; actual_model_reference_boxes = sum(len(references[name]) for name, _ in model_maps)
        report.update(status="completed", safety_result="PASS" if not failures and actual_model_reference_boxes > 0 else "FAIL", total_cases=len(rows),
                      synthetic_cases=len(rows) - len(model_maps), actual_model_cases=len(model_maps), actual_model_reference_boxes=actual_model_reference_boxes,
                      failed_cases=len(failures), thresholds={"minimum_iou": IOU_ACCEPTANCE, "maximum_center_error_pixels": CENTER_ACCEPTANCE_PIXELS,
                      "maximum_score_absolute_error": SCORE_ACCEPTANCE}, model=model_metadata, rows=rows)
        exit_code = 0 if report["safety_result"] == "PASS" else 1
    except Exception as error:
        report.update(status="failed", safety_result="FAIL", error_type=type(error).__name__, error=str(error)); exit_code = 1
    args.report.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8"); print(json.dumps(report, sort_keys=True)); return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
