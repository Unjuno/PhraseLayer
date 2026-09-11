#!/usr/bin/env python3
"""Measure whether DB quad geometry drift changes real PP-OCR recognition output.

Host-only experiment:
  rendered synthetic text -> pinned detector ONNX -> Paddle/OpenCV reference DB boxes
                                    -> production Core DB boxes
  same source image + each matched quad -> Paddle-compatible crop/resize -> pinned recognizer ONNX -> CTC decode

The experiment compares the two geometry paths, not human OCR accuracy. No image, model, dictionary, or tensor
is uploaded by the workflow; the report contains only aggregate metrics and hashes of decoded text.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import platform
import subprocess

import cv2
import numpy as np
import onnxruntime as ort

import run_ppocr_detector_db_differential as db

ROOT = Path(__file__).resolve().parents[1]
REC_WIDTH = 320
REC_HEIGHT = 48
TEXT_TOLERANCE = 0
# PhraseLayer/PaddleOCR production contract: recognizer candidates are retained at score >= 0.5.
# A raw confidence delta is diagnostic; only a change in this real product decision is a correctness failure.
RECOGNITION_DROP_SCORE = 0.5


def check(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def load_dictionary(path: Path) -> list[str]:
    raw = path.read_bytes()
    lock = json.loads((ROOT / "models/models.lock.json").read_text(encoding="utf-8"))
    candidate = next(item for item in lock["candidates"] if item["id"] == "pp-ocrv6-tiny-rec")
    contract = candidate["recognition_dictionary"]
    check(hashlib.sha256(raw).hexdigest() == contract["generated_artifact_sha256"], "Dictionary identity differs from lock")
    tokens = raw.decode("utf-8").removesuffix("\n").split("\n")
    check(len(tokens) == contract["raw_token_count"], "Unexpected raw dictionary count")
    if contract["use_space_char"]:
        tokens.append(" ")
    check(len(tokens) == contract["effective_token_count"], "Unexpected effective dictionary count")
    return tokens


def runtime(path: Path, model_id: str) -> ort.InferenceSession:
    lock = json.loads((ROOT / "models/models.lock.json").read_text(encoding="utf-8"))
    candidate = next(item for item in lock["candidates"] if item["id"] == model_id)
    raw = path.read_bytes()
    check(len(raw) == candidate["artifact_size_bytes"], f"{model_id} size differs from lock")
    check(hashlib.sha256(raw).hexdigest() == candidate["artifact_sha256"], f"{model_id} SHA-256 differs from lock")
    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    options.inter_op_num_threads = 1
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    return ort.InferenceSession(raw, options, providers=["CPUExecutionProvider"])


def order_box(points: np.ndarray) -> np.ndarray:
    # Detector outputs are already Paddle-ordered. Keep the routine explicit and reject malformed input.
    result = np.asarray(points, dtype=np.float32).reshape(4, 2)
    check(np.isfinite(result).all(), "Quad contains non-finite coordinates")
    return result


def rectify(image: np.ndarray, points: np.ndarray) -> np.ndarray:
    points = order_box(points)
    width = int(max(np.linalg.norm(points[0] - points[1]), np.linalg.norm(points[2] - points[3])))
    height = int(max(np.linalg.norm(points[0] - points[3]), np.linalg.norm(points[1] - points[2])))
    check(width > 0 and height > 0, "Paddle crop would have zero extent")
    destination = np.float32([[0, 0], [width, 0], [width, height], [0, height]])
    matrix = cv2.getPerspectiveTransform(points, destination)
    crop = cv2.warpPerspective(
        image,
        matrix,
        (width, height),
        flags=cv2.INTER_CUBIC,
        borderMode=cv2.BORDER_REPLICATE,
    )
    if crop.shape[0] / float(crop.shape[1]) >= 1.5:
        crop = np.rot90(crop)
    return np.ascontiguousarray(crop)


def recognizer_input(crop: np.ndarray) -> np.ndarray:
    height, width = crop.shape[:2]
    check(height > 0 and width > 0 and crop.ndim == 3 and crop.shape[2] == 3, "Recognizer crop must be BGR HWC")
    resized_width = min(REC_WIDTH, max(1, int(np.ceil(REC_HEIGHT * (width / float(height))))))
    resized = cv2.resize(crop, (resized_width, REC_HEIGHT), interpolation=cv2.INTER_LINEAR).astype(np.float32)
    normalized = np.transpose(resized, (2, 0, 1)) / np.float32(255.0)
    normalized = (normalized - np.float32(0.5)) / np.float32(0.5)
    padded = np.zeros((3, REC_HEIGHT, REC_WIDTH), dtype=np.float32)
    padded[:, :, :resized_width] = normalized
    return padded[None]


def decode(output: np.ndarray, dictionary: list[str]) -> tuple[str, float, list[int]]:
    values = np.asarray(output, dtype=np.float32)
    check(values.ndim == 3 and values.shape[0] == 1 and values.shape[2] == len(dictionary) + 1,
          "Recognizer output violates [1,time,dictionary+blank]")
    check(np.isfinite(values).all(), "Recognizer output contains non-finite values")
    indices = values.argmax(axis=-1)[0]
    scores = values.max(axis=-1)[0]
    emitted: list[int] = []
    chars: list[str] = []
    emitted_scores: list[float] = []
    for time, class_index in enumerate(indices):
        class_index = int(class_index)
        if class_index == 0 or (time > 0 and class_index == int(indices[time - 1])):
            continue
        emitted.append(class_index)
        chars.append(dictionary[class_index - 1])
        emitted_scores.append(float(scores[time]))
    confidence = sum(emitted_scores) / len(emitted_scores) if emitted_scores else 0.0
    return "".join(chars), confidence, emitted


def text_hash(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def make_fixture(name: str, lines: list[str], angle: float = 0.0, scale_override: float | None = None) -> tuple[str, np.ndarray, list[str]]:
    width = height = 736
    base = np.full((height, width, 3), 255, dtype=np.uint8)
    y = 190
    for line in lines:
        scale = scale_override if scale_override is not None else (2.2 if len(line) < 12 else 1.5)
        thickness = 5 if scale >= 1.2 else 3
        (text_width, text_height), _ = cv2.getTextSize(line, cv2.FONT_HERSHEY_SIMPLEX, scale, thickness)
        x = max(12, (width - text_width) // 2)
        cv2.putText(base, line, (x, y), cv2.FONT_HERSHEY_SIMPLEX, scale, (0, 0, 0), thickness, cv2.LINE_AA)
        y += text_height + 130
    if angle:
        transform = cv2.getRotationMatrix2D((width / 2.0, height / 2.0), angle, 1.0)
        base = cv2.warpAffine(base, transform, (width, height), flags=cv2.INTER_CUBIC,
                              borderMode=cv2.BORDER_CONSTANT, borderValue=(255, 255, 255))
    return name, base, lines


def fixtures() -> list[tuple[str, np.ndarray, list[str]]]:
    return [
        make_fixture("keep-off", ["KEEP OFF"]),
        make_fixture("emergency-exit", ["EMERGENCY EXIT"]),
        make_fixture("two-lines", ["KEEP OFF", "EMERGENCY EXIT"]),
        make_fixture("mixed-short", ["ROOM 204", "EXIT"]),
        make_fixture("tilt-pos-12", ["KEEP OFF"], 12.0),
        make_fixture("tilt-neg-18", ["EMERGENCY EXIT"], -18.0),
        make_fixture("tilt-pos-31-small", ["EXIT"], 31.0, 0.9),
        make_fixture("tilt-neg-37-small", ["ROOM 204"], -37.0, 0.9),
        make_fixture("small-two-lines", ["KEEP OFF", "EXIT"], 7.0, 0.8),
    ]


def match(reference: list[dict], core: list[dict]) -> list[tuple[dict, dict, float]]:
    unmatched = set(range(len(core)))
    result = []
    for expected in reference:
        check(unmatched, "Core has fewer boxes than reference")
        ranked = sorted(((db.polygon_iou(expected["points"], core[index]["points"]), index) for index in unmatched), reverse=True)
        iou, index = ranked[0]
        unmatched.remove(index)
        result.append((expected, core[index], iou))
    check(not unmatched, "Core has more boxes than reference")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--detector", type=Path, required=True)
    parser.add_argument("--recognizer", type=Path, required=True)
    parser.add_argument("--dictionary", type=Path, required=True)
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    args.work_dir.mkdir(parents=True, exist_ok=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)

    det = runtime(args.detector, "pp-ocrv6-tiny-det")
    rec = runtime(args.recognizer, "pp-ocrv6-tiny-rec")
    dictionary = load_dictionary(args.dictionary)
    det_input = det.get_inputs()[0]
    rec_input = rec.get_inputs()[0]
    check(rec_input.shape[2] == REC_HEIGHT, "Recognizer height differs from reviewed contract")

    images: dict[str, np.ndarray] = {}
    reference: dict[str, list[dict]] = {}
    manifest_cases: list[dict] = []
    expected_lines: dict[str, list[str]] = {}
    for index, (name, image, lines) in enumerate(fixtures()):
        images[name] = image
        expected_lines[name] = lines
        probability = np.asarray(det.run(None, {det_input.name: db.detector_input(image)})[0], dtype=np.float32)
        if probability.ndim == 4:
            probability = probability[0, 0]
        elif probability.ndim == 3:
            probability = probability[0]
        check(probability.ndim == 2, "Unexpected detector map rank")
        reference[name] = db.reference_boxes(probability, image.shape[1], image.shape[0])
        map_file = f"map-{index:02d}.f32"
        probability.astype("<f4", copy=False).tofile(args.work_dir / map_file)
        manifest_cases.append({"name": name, "width": int(probability.shape[1]), "height": int(probability.shape[0]),
                               "destination_width": int(image.shape[1]), "destination_height": int(image.shape[0]), "map_file": map_file})

    manifest = args.work_dir / "manifest.json"
    core_path = args.work_dir / "core.json"
    manifest.write_text(json.dumps({"cases": manifest_cases}), encoding="utf-8")
    subprocess.run(["dotnet", "run", "--project",
                    str(ROOT / "experiments/PhraseLayer.DetectorOutputReplay/PhraseLayer.DetectorOutputReplay.csproj"),
                    "-c", "Release", "--", str(manifest), str(core_path)], check=True)
    core = json.loads(core_path.read_text(encoding="utf-8"))
    core_by_name = {row["name"]: row["detections"] for row in core["cases"]}

    rows = []
    text_mismatches = 0
    drop_decision_mismatches = 0
    reference_ground_truth_matches = 0
    core_ground_truth_matches = 0
    compared = 0
    max_confidence_delta = 0.0
    max_crop_width_delta = 0
    max_crop_height_delta = 0
    min_iou = 1.0

    for name, image, _ in fixtures():
        refs = reference[name]
        cores = core_by_name[name]
        check(len(refs) == len(cores), f"{name}: detector count differs before recognition")
        pairs = match(refs, cores)
        expected = expected_lines[name]
        for box_index, (ref_box, core_box, iou) in enumerate(pairs):
            ref_crop = rectify(image, np.asarray(ref_box["points"], dtype=np.float32))
            core_crop = rectify(image, np.asarray(core_box["points"], dtype=np.float32))
            ref_output = rec.run(None, {rec_input.name: recognizer_input(ref_crop)})[0]
            core_output = rec.run(None, {rec_input.name: recognizer_input(core_crop)})[0]
            ref_text, ref_conf, ref_indices = decode(ref_output, dictionary)
            core_text, core_conf, core_indices = decode(core_output, dictionary)
            same_text = ref_text == core_text
            if not same_text:
                text_mismatches += 1
            reference_retained = ref_conf >= RECOGNITION_DROP_SCORE
            core_retained = core_conf >= RECOGNITION_DROP_SCORE
            if reference_retained != core_retained:
                drop_decision_mismatches += 1
            max_confidence_delta = max(max_confidence_delta, abs(ref_conf - core_conf))
            max_crop_width_delta = max(max_crop_width_delta, abs(ref_crop.shape[1] - core_crop.shape[1]))
            max_crop_height_delta = max(max_crop_height_delta, abs(ref_crop.shape[0] - core_crop.shape[0]))
            min_iou = min(min_iou, iou)
            # Detection order is Paddle contour order, not reading order; ground truth is diagnostic only.
            reference_gt = ref_text in expected
            core_gt = core_text in expected
            reference_ground_truth_matches += int(reference_gt)
            core_ground_truth_matches += int(core_gt)
            compared += 1
            rows.append({
                "fixture": name,
                "box_index": box_index,
                "quad_iou": iou,
                "reference_crop_width": int(ref_crop.shape[1]),
                "reference_crop_height": int(ref_crop.shape[0]),
                "core_crop_width": int(core_crop.shape[1]),
                "core_crop_height": int(core_crop.shape[0]),
                "text_exact": same_text,
                "reference_confidence": ref_conf,
                "core_confidence": core_conf,
                "confidence_absolute_delta": abs(ref_conf - core_conf),
                "reference_retained_at_product_threshold": reference_retained,
                "core_retained_at_product_threshold": core_retained,
                "reference_token_count": len(ref_indices),
                "core_token_count": len(core_indices),
                "reference_text_sha256": text_hash(ref_text),
                "core_text_sha256": text_hash(core_text),
                "reference_matches_fixture_line": reference_gt,
                "core_matches_fixture_line": core_gt,
            })

    safety = (
        text_mismatches == TEXT_TOLERANCE
        and drop_decision_mismatches == 0
        and reference_ground_truth_matches == compared
        and core_ground_truth_matches == compared
    )
    report = {
        "experiment": "ppocr-real-model-quad-impact-differential",
        "status": "completed",
        "safety_result": "PASS" if safety else "FAIL",
        "fixtures": len(fixtures()),
        "compared_boxes": compared,
        "text_mismatches": text_mismatches,
        "recognition_drop_score": RECOGNITION_DROP_SCORE,
        "drop_decision_mismatches": drop_decision_mismatches,
        "maximum_confidence_absolute_delta": max_confidence_delta,
        "confidence_delta_is_diagnostic_not_acceptance_criterion": True,
        "minimum_quad_iou": min_iou,
        "maximum_crop_width_delta_pixels": max_crop_width_delta,
        "maximum_crop_height_delta_pixels": max_crop_height_delta,
        "reference_ground_truth_line_matches": reference_ground_truth_matches,
        "core_ground_truth_line_matches": core_ground_truth_matches,
        "rows": rows,
        "python": platform.python_version(),
        "onnxruntime_version": ort.__version__,
        "opencv_version": cv2.__version__,
        "provider": ["CPUExecutionProvider"],
        "real_detector_model_execution_performed": True,
        "real_recognizer_model_execution_performed": True,
        "real_unity_execution_performed": False,
        "gpu_execution_performed": False,
        "quest_execution_performed": False,
        "natural_image_accuracy_measured": False,
        "latency_measured": False,
        "human_learning_effectiveness_measured": False,
    }
    args.report.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(report, sort_keys=True))
    return 0 if safety else 1


if __name__ == "__main__":
    raise SystemExit(main())
