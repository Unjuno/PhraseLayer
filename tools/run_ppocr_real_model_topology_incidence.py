#!/usr/bin/env python3
"""Measure whether Paddle-only child contours occur on real PP-OCR detector outputs.

Host-only experiment. Synthetic English lines rich in glyph holes are rendered under deterministic
size/rotation/contrast/blur variants and passed through the exact pinned detector ONNX. Paddle's
OpenCV RETR_TREE topology is inspected. Any accepted child contour is cropped from the same source
image and passed through the exact pinned recognizer ONNX to determine whether the child would survive
PhraseLayer's real recognition-drop threshold (0.5).

This measures topology incidence on synthetic text rendered through the real models. It is not a
natural-image OCR accuracy, Unity/GPU, Quest, latency, thermal, or human-learning measurement.
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
import run_ppocr_e2e_quad_impact as e2e

ROOT = Path(__file__).resolve().parents[1]
DROP_SCORE = 0.5

TEXTS = (
    "OOO",
    "000",
    "888",
    "BROAD",
    "ROOM 808",
    "PARK",
    "DANGER",
    "A0B8",
    "PROHIBITED",
    "DOOR",
    "OPEN",
    "NO PARKING",
)

# angle_deg, font_scale, gray, blur_sigma
VARIANTS = (
    (0.0, 0.65, 0, 0.0),
    (0.0, 1.00, 0, 0.0),
    (0.0, 1.80, 0, 0.0),
    (15.0, 1.20, 20, 0.0),
    (-20.0, 1.20, 45, 0.0),
    (32.0, 0.90, 0, 0.0),
    (-35.0, 0.90, 70, 0.0),
    (8.0, 1.00, 95, 1.1),
)


def check(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def model_session(path: Path, model_id: str) -> ort.InferenceSession:
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


def load_dictionary(path: Path) -> list[str]:
    raw = path.read_bytes()
    lock = json.loads((ROOT / "models/models.lock.json").read_text(encoding="utf-8"))
    candidate = next(item for item in lock["candidates"] if item["id"] == "pp-ocrv6-tiny-rec")
    contract = candidate["recognition_dictionary"]
    check(hashlib.sha256(raw).hexdigest() == contract["generated_artifact_sha256"], "Dictionary identity differs from lock")
    tokens = raw.decode("utf-8").removesuffix("\n").split("\n")
    check(len(tokens) == contract["raw_token_count"], "Unexpected raw dictionary token count")
    if contract["use_space_char"]:
        tokens.append(" ")
    check(len(tokens) == contract["effective_token_count"], "Unexpected effective dictionary token count")
    return tokens


def render(text: str, angle: float, scale: float, gray: int, blur_sigma: float) -> np.ndarray:
    width = height = 736
    image = np.full((height, width, 3), 255, dtype=np.uint8)
    thickness = max(1, int(round(scale * 3.0)))
    (text_width, text_height), _ = cv2.getTextSize(text, cv2.FONT_HERSHEY_SIMPLEX, scale, thickness)
    x = max(8, (width - text_width) // 2)
    y = max(text_height + 8, (height + text_height) // 2)
    cv2.putText(image, text, (x, y), cv2.FONT_HERSHEY_SIMPLEX, scale, (gray, gray, gray), thickness, cv2.LINE_AA)
    if angle:
        matrix = cv2.getRotationMatrix2D((width / 2.0, height / 2.0), angle, 1.0)
        image = cv2.warpAffine(image, matrix, (width, height), flags=cv2.INTER_CUBIC,
                               borderMode=cv2.BORDER_CONSTANT, borderValue=(255, 255, 255))
    if blur_sigma > 0:
        image = cv2.GaussianBlur(image, (0, 0), blur_sigma)
    return image


def accepted_contours(prediction: np.ndarray, dest_width: int, dest_height: int) -> list[dict]:
    bitmap = prediction > db.BITMAP_THRESHOLD
    found = cv2.findContours((bitmap * 255).astype(np.uint8), cv2.RETR_TREE, cv2.CHAIN_APPROX_SIMPLE)
    contours = found[0] if len(found) == 2 else found[1]
    hierarchy = found[1] if len(found) == 2 else found[2]
    hierarchy = hierarchy[0] if hierarchy is not None and len(hierarchy) else np.empty((0, 4), dtype=np.int32)
    height, width = bitmap.shape
    result: list[dict] = []
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
        final_box, final_short = db.mini_box(expanded_array)
        if final_short < db.MIN_SIZE + 2.0:
            continue
        final_box = np.asarray(final_box, dtype=np.float32)
        final_box[:, 0] = np.clip(np.round(final_box[:, 0] / width * dest_width), 0, dest_width)
        final_box[:, 1] = np.clip(np.round(final_box[:, 1] / height * dest_height), 0, dest_height)
        parent = int(hierarchy[index][3]) if index < len(hierarchy) else -1
        result.append({
            "contour_index": index,
            "parent_index": parent,
            "is_child_contour": parent >= 0,
            "score": float(score),
            "points": final_box.astype("int32").astype(float).tolist(),
        })
    return result


def hash_text(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


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

    det = model_session(args.detector, "pp-ocrv6-tiny-det")
    rec = model_session(args.recognizer, "pp-ocrv6-tiny-rec")
    dictionary = load_dictionary(args.dictionary)
    det_input = det.get_inputs()[0]
    rec_input = rec.get_inputs()[0]

    manifest_cases: list[dict] = []
    fixture_records: list[dict] = []
    images: dict[str, np.ndarray] = {}
    accepted_by_name: dict[str, list[dict]] = {}
    child_total = 0
    child_retained = 0
    child_nonempty = 0
    child_retained_nonempty = 0
    child_exact_full_line = 0
    child_substring_of_line = 0
    maximum_child_confidence = 0.0
    child_rows: list[dict] = []

    fixture_index = 0
    for text in TEXTS:
        for variant_index, (angle, scale, gray, blur_sigma) in enumerate(VARIANTS):
            name = f"fixture-{fixture_index:03d}"
            fixture_index += 1
            image = render(text, angle, scale, gray, blur_sigma)
            images[name] = image
            output = np.asarray(det.run(None, {det_input.name: db.detector_input(image)})[0], dtype=np.float32)
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
            accepted = accepted_contours(probability, image.shape[1], image.shape[0])
            accepted_by_name[name] = accepted
            map_file = f"map-{fixture_index - 1:03d}.f32"
            probability.astype("<f4", copy=False).tofile(args.work_dir / map_file)
            manifest_cases.append({
                "name": name,
                "width": int(probability.shape[1]),
                "height": int(probability.shape[0]),
                "destination_width": int(image.shape[1]),
                "destination_height": int(image.shape[0]),
                "map_file": map_file,
            })
            fixture_records.append({
                "name": name,
                "source_text_sha256": hash_text(text),
                "source_length": len(text),
                "variant_index": variant_index,
                "angle_degrees": angle,
                "font_scale": scale,
                "gray": gray,
                "blur_sigma": blur_sigma,
                "accepted_reference_contours": len(accepted),
                "accepted_child_contours": sum(1 for row in accepted if row["is_child_contour"]),
            })

            for contour in accepted:
                if not contour["is_child_contour"]:
                    continue
                child_total += 1
                crop = e2e.rectify(image, np.asarray(contour["points"], dtype=np.float32))
                rec_output = rec.run(None, {rec_input.name: e2e.recognizer_input(crop)})[0]
                decoded, confidence, indices = e2e.decode(rec_output, dictionary)
                retained = confidence >= DROP_SCORE
                nonempty = len(decoded) > 0
                exact = decoded == text
                substring = nonempty and decoded in text
                child_retained += int(retained)
                child_nonempty += int(nonempty)
                child_retained_nonempty += int(retained and nonempty)
                child_exact_full_line += int(exact)
                child_substring_of_line += int(substring)
                maximum_child_confidence = max(maximum_child_confidence, confidence)
                child_rows.append({
                    "fixture": name,
                    "variant_index": variant_index,
                    "detector_score": contour["score"],
                    "recognition_confidence": confidence,
                    "retained_at_0_5": retained,
                    "decoded_nonempty": nonempty,
                    "decoded_exact_full_source": exact,
                    "decoded_is_source_substring": substring,
                    "decoded_token_count": len(indices),
                    "decoded_text_sha256": hash_text(decoded),
                    "crop_width": int(crop.shape[1]),
                    "crop_height": int(crop.shape[0]),
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

    count_mismatch_fixtures = 0
    reference_extra_candidates = 0
    core_extra_candidates = 0
    fixtures_with_child_contours = 0
    fixtures_with_retained_child = 0
    for fixture in fixture_records:
        name = fixture["name"]
        reference_count = fixture["accepted_reference_contours"]
        core_count = core_counts[name]
        fixture["core_candidate_count"] = core_count
        fixture["candidate_count_equal"] = reference_count == core_count
        delta = reference_count - core_count
        if delta != 0:
            count_mismatch_fixtures += 1
            reference_extra_candidates += max(0, delta)
            core_extra_candidates += max(0, -delta)
        if fixture["accepted_child_contours"] > 0:
            fixtures_with_child_contours += 1
        if any(row["fixture"] == name and row["retained_at_0_5"] for row in child_rows):
            fixtures_with_retained_child += 1

    # This is a diagnostic, not a compatibility gate. The key outcome is whether Paddle-only child contours
    # are present and whether the real recognizer would retain them. Production is not modified here.
    report = {
        "experiment": "ppocr-real-model-topology-incidence",
        "status": "completed",
        "synthetic_source_images": len(fixture_records),
        "source_strings": len(TEXTS),
        "variants_per_string": len(VARIANTS),
        "fixtures_with_accepted_child_contours": fixtures_with_child_contours,
        "accepted_child_contours": child_total,
        "child_contours_with_nonempty_decode": child_nonempty,
        "child_contours_retained_at_0_5": child_retained,
        "child_contours_retained_and_nonempty": child_retained_nonempty,
        "child_contours_decoding_exact_full_source": child_exact_full_line,
        "child_contours_decoding_source_substring": child_substring_of_line,
        "fixtures_with_retained_child": fixtures_with_retained_child,
        "maximum_child_recognition_confidence": maximum_child_confidence,
        "candidate_count_mismatch_fixtures": count_mismatch_fixtures,
        "reference_extra_candidates": reference_extra_candidates,
        "core_extra_candidates": core_extra_candidates,
        "recognition_drop_score": DROP_SCORE,
        "fixture_rows": fixture_records,
        "child_rows": child_rows,
        "python": platform.python_version(),
        "onnxruntime_version": ort.__version__,
        "opencv_version": cv2.__version__,
        "provider": ["CPUExecutionProvider"],
        "real_detector_model_execution_performed": True,
        "real_recognizer_model_execution_performed": child_total > 0,
        "real_unity_execution_performed": False,
        "gpu_execution_performed": False,
        "quest_execution_performed": False,
        "natural_image_accuracy_measured": False,
        "latency_measured": False,
        "human_learning_effectiveness_measured": False,
        "production_change_applied": False,
    }
    args.report.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(report, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
