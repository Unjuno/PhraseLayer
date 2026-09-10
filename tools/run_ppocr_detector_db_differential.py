#!/usr/bin/env python3
"""Compare production Core DB quad postprocessing with OpenCV/pyclipper on identical maps.

The audit has two sources of probability maps:
1. deterministic synthetic maps that isolate DB geometry semantics; and
2. the exact pinned PP-OCRv6 tiny detector executed with ONNX Runtime CPUExecutionProvider.

Model bytes, images and probability maps remain local to the CI workspace and are deleted by the workflow.
The output report contains only aggregate metrics and small box-coordinate summaries.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import math
import os
from pathlib import Path
import platform
import subprocess
import sys

import cv2
import numpy as np
import onnx
import onnxruntime as ort
import pyclipper

ROOT = Path(__file__).resolve().parents[1]
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
    points = sorted(points.tolist(), key=lambda point: point[0])
    if points[1][1] > points[0][1]:
        index_1, index_4 = 0, 1
    else:
        index_1, index_4 = 1, 0
    if points[3][1] > points[2][1]:
        index_2, index_3 = 2, 3
    else:
        index_2, index_3 = 3, 2
    return np.asarray([points[index_1], points[index_2], points[index_3], points[index_4]], dtype=np.float32)


def mini_box(contour: np.ndarray) -> tuple[np.ndarray, float]:
    rectangle = cv2.minAreaRect(contour.astype(np.float32))
    return order_box(cv2.boxPoints(rectangle)), float(min(rectangle[1]))


def polygon_area_and_perimeter(points: np.ndarray) -> tuple[float, float]:
    contour = points.astype(np.float32).reshape((-1, 1, 2))
    return abs(float(cv2.contourArea(contour))), float(cv2.arcLength(contour, True))


def box_score_fast(prediction: np.ndarray, box: np.ndarray) -> float:
    height, width = prediction.shape
    xmin = max(0, int(math.floor(float(box[:, 0].min()))))
    xmax = min(width - 1, int(math.ceil(float(box[:, 0].max()))))
    ymin = max(0, int(math.floor(float(box[:, 1].min()))))
    ymax = min(height - 1, int(math.ceil(float(box[:, 1].max()))))
    if xmax < xmin or ymax < ymin:
        return 0.0
    local = box.copy()
    local[:, 0] -= xmin
    local[:, 1] -= ymin
    mask = np.zeros((ymax - ymin + 1, xmax - xmin + 1), dtype=np.uint8)
    cv2.fillPoly(mask, [local.astype(np.int32)], 1)
    return float(cv2.mean(prediction[ymin : ymax + 1, xmin : xmax + 1], mask)[0])


def unclip(points: np.ndarray) -> np.ndarray:
    area, perimeter = polygon_area_and_perimeter(points)
    if perimeter <= 0:
        return np.empty((0, 2), dtype=np.float32)
    distance = area * UNCLIP_RATIO / perimeter
    offset = pyclipper.PyclipperOffset()
    # PaddleOCR passes the min-area box through pyclipper. Rounding here is explicit because
    # pyclipper is integer geometry; it also makes the host oracle stable across Python builds.
    path = [(int(round(float(x))), int(round(float(y)))) for x, y in points]
    offset.AddPath(path, pyclipper.JT_ROUND, pyclipper.ET_CLOSEDPOLYGON)
    expanded = offset.Execute(distance)
    if not expanded:
        return np.empty((0, 2), dtype=np.float32)
    return np.asarray([point for polygon in expanded for point in polygon], dtype=np.float32)


def reference_boxes(prediction: np.ndarray, destination_width: int, destination_height: int) -> list[dict]:
    bitmap = (prediction > BITMAP_THRESHOLD).astype(np.uint8) * 255
    found = cv2.findContours(bitmap, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
    contours = found[0] if len(found) == 2 else found[1]
    height, width = prediction.shape
    output: list[dict] = []
    for contour in contours[:MAX_CANDIDATES]:
        box, short_side = mini_box(contour)
        if short_side < MIN_SIZE:
            continue
        score = box_score_fast(prediction, box)
        if score < BOX_THRESHOLD:
            continue
        expanded = unclip(box)
        if len(expanded) < 3:
            continue
        final_box, final_short_side = mini_box(expanded.reshape((-1, 1, 2)))
        if final_short_side < MIN_SIZE + 2.0:
            continue
        final_box[:, 0] = np.clip(np.round(final_box[:, 0] / width * destination_width), 0, destination_width)
        final_box[:, 1] = np.clip(np.round(final_box[:, 1] / height * destination_height), 0, destination_height)
        output.append({"score": score, "points": final_box.astype(np.float64).tolist()})
    return output


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
        count = 1 + (index % 3)
        for _ in range(count):
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
    stds = np.asarray([0.229, 0.224, 0.225], dtype=np.float32)
    values = (values - means) / stds
    return np.transpose(values, (2, 0, 1))[None, :, :, :].astype(np.float32)


def actual_model_maps(model_path: Path) -> tuple[list[tuple[str, np.ndarray]], dict]:
    candidate = model_candidate()
    raw = model_path.read_bytes()
    check(len(raw) == candidate["artifact_size_bytes"], "Detector model size differs from lock")
    check(hashlib.sha256(raw).hexdigest() == candidate["artifact_sha256"], "Detector model SHA-256 differs from lock")
    model = onnx.load_model_from_string(raw)
    onnx.checker.check_model(model)
    options = ort.SessionOptions(); options.intra_op_num_threads = 1; options.inter_op_num_threads = 1
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    runtime = ort.InferenceSession(raw, options, providers=["CPUExecutionProvider"])
    inputs = runtime.get_inputs(); check(len(inputs) == 1 and inputs[0].type == "tensor(float)", "Detector must have one float input")
    fixtures = [
        ("model-keep-off", ["KEEP OFF"]),
        ("model-emergency-exit", ["EMERGENCY EXIT"]),
        ("model-two-lines", ["KEEP OFF", "EMERGENCY EXIT"]),
        ("model-mixed-short", ["ROOM 204", "EXIT"]),
    ]
    maps: list[tuple[str, np.ndarray]] = []
    for name, lines in fixtures:
        tensor = detector_input(render_detector_fixture(lines))
        outputs = runtime.run(None, {inputs[0].name: tensor})
        check(len(outputs) >= 1, "Detector produced no output tensors")
        output = np.asarray(outputs[0], dtype=np.float32)
        if output.ndim == 4:
            check(output.shape[0] == 1 and output.shape[1] == 1, "Rank-4 detector output must be [1,1,H,W]")
            probability = output[0, 0]
        elif output.ndim == 3:
            check(output.shape[0] == 1, "Rank-3 detector output must be [1,H,W]")
            probability = output[0]
        elif output.ndim == 2:
            probability = output
        else:
            raise ValueError(f"Unsupported detector output rank: {output.shape}")
        check(np.isfinite(probability).all(), "Detector output contains non-finite values")
        check(np.logical_and(probability >= 0, probability <= 1).all(), "Detector output is not a probability map")
        maps.append((name, probability.copy()))
    return maps, {"model_revision": candidate["revision"], "model_sha256": hashlib.sha256(raw).hexdigest(),
                  "provider": runtime.get_providers(), "input_shape": inputs[0].shape,
                  "onnx_ir_version": model.ir_version,
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
    unmatched = set(range(len(actual)))
    matches = []
    for expected in reference:
        if not unmatched:
            break
        ranked = sorted(((polygon_iou(expected["points"], actual[index]["points"]), index) for index in unmatched), reverse=True)
        iou, index = ranked[0]; unmatched.remove(index)
        observed = actual[index]
        matches.append({"iou": iou,
                        "center_error_pixels": float(np.linalg.norm(center(expected["points"]) - center(observed["points"]))),
                        "score_absolute_error": abs(float(expected["score"]) - float(observed["score"]))})
    count_equal = len(reference) == len(actual)
    minimum_iou = min((row["iou"] for row in matches), default=(1.0 if not reference and not actual else 0.0))
    maximum_center = max((row["center_error_pixels"] for row in matches), default=0.0)
    maximum_score = max((row["score_absolute_error"] for row in matches), default=0.0)
    passed = count_equal and len(matches) == len(reference) and minimum_iou >= IOU_ACCEPTANCE and maximum_center <= CENTER_ACCEPTANCE_PIXELS and maximum_score <= SCORE_ACCEPTANCE
    return {"reference_count": len(reference), "core_count": len(actual), "count_equal": count_equal,
            "minimum_iou": minimum_iou, "maximum_center_error_pixels": maximum_center,
            "maximum_score_absolute_error": maximum_score, "passed": passed}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    args.work_dir.mkdir(parents=True, exist_ok=True); args.report.parent.mkdir(parents=True, exist_ok=True)
    report = {"experiment": "ppocr-detector-db-opencv-production-core-differential", "status": "uncertain",
              "seed": 20260911, "opencv_version": cv2.__version__, "pyclipper_version": importlib.metadata.version("pyclipper"),
              "onnxruntime_version": ort.__version__, "python": platform.python_version(), "architecture": platform.machine(),
              "real_unity_execution_performed": False, "gpu_execution_performed": False, "quest_execution_performed": False,
              "natural_image_accuracy_measured": False, "latency_measured": False}
    try:
        model_maps, model_metadata = actual_model_maps(args.model)
        all_maps = synthetic_maps() + model_maps
        manifest_cases = []
        references: dict[str, list[dict]] = {}
        for index, (name, probability) in enumerate(all_maps):
            map_file = f"map-{index:03d}.f32"
            probability.astype("<f4", copy=False).tofile(args.work_dir / map_file)
            references[name] = reference_boxes(probability, probability.shape[1], probability.shape[0])
            manifest_cases.append({"name": name, "width": int(probability.shape[1]), "height": int(probability.shape[0]),
                                   "destination_width": int(probability.shape[1]), "destination_height": int(probability.shape[0]),
                                   "map_file": map_file})
        manifest_path = args.work_dir / "manifest.json"; core_path = args.work_dir / "core.json"
        manifest_path.write_text(json.dumps({"cases": manifest_cases}), encoding="utf-8")
        subprocess.run(["dotnet", "run", "--project", str(ROOT / "experiments/PhraseLayer.DetectorOutputReplay/PhraseLayer.DetectorOutputReplay.csproj"),
                        "-c", "Release", "--", str(manifest_path), str(core_path)], check=True)
        core = json.loads(core_path.read_text(encoding="utf-8"))
        actual_by_name = {row["name"]: row["detections"] for row in core["cases"]}
        rows = []
        for name, _ in all_maps:
            result = compare_case(references[name], actual_by_name[name])
            result["name"] = name; result["actual_model_output"] = name.startswith("model-")
            rows.append(result)
        actual_model_reference_boxes = sum(len(references[name]) for name, _ in model_maps)
        failures = [row for row in rows if not row["passed"]]
        report.update(status="completed", safety_result="PASS" if not failures and actual_model_reference_boxes > 0 else "FAIL",
                      total_cases=len(rows), synthetic_cases=len(rows) - len(model_maps), actual_model_cases=len(model_maps),
                      actual_model_reference_boxes=actual_model_reference_boxes, failed_cases=len(failures),
                      thresholds={"minimum_iou": IOU_ACCEPTANCE, "maximum_center_error_pixels": CENTER_ACCEPTANCE_PIXELS,
                                  "maximum_score_absolute_error": SCORE_ACCEPTANCE}, model=model_metadata, rows=rows)
        result = 0 if report["safety_result"] == "PASS" else 1
    except Exception as error:
        report.update(status="failed", safety_result="FAIL", error_type=type(error).__name__, error=str(error))
        result = 1
    args.report.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(report, sort_keys=True))
    return result


if __name__ == "__main__":
    raise SystemExit(main())
