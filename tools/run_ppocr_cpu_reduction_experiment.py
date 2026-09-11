"""Execute the locked recognizer and replay its packed output through production C# Core.

This is a CPUExecutionProvider differential experiment. It does NOT run Unity's FunctionalGraph,
TextureConverter, shaders, camera, or a Quest device. No model bytes are written into evidence.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import sys

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper
import onnxruntime as ort

ROOT = Path(__file__).resolve().parents[1]
PREFIX = "phraselayer_cpu_audit_"
TOLERANCE = 1e-6


def check(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def session(model: onnx.ModelProto) -> ort.InferenceSession:
    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    options.inter_op_num_threads = 1
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    return ort.InferenceSession(model.SerializeToString(), options, providers=["CPUExecutionProvider"])


def pack_graph(model: onnx.ModelProto) -> onnx.ModelProto:
    result = copy.deepcopy(model)
    check(len(result.graph.output) == 1, "The reviewed recognizer must have exactly one output")
    opset = next(item.version for item in result.opset_import if item.domain in ("", "ai.onnx"))
    check(opset >= 11, "The experiment requires ONNX opset 11 or later")
    names = {name for node in result.graph.node for name in [*node.input, *node.output]}
    check(not any(name.startswith(PREFIX) for name in names), "Audit node prefix already exists")
    source = result.graph.output[0].name
    index = PREFIX + "index"
    score = PREFIX + "score"
    axes = PREFIX + "axes"
    attrs = {"axis": -1, "keepdims": 0}
    if opset >= 12:
        attrs["select_last_index"] = 0
    nodes = [helper.make_node("ArgMax", [source], [index], **attrs),
             helper.make_node("Cast", [index], [index + "_float"], to=TensorProto.FLOAT)]
    if opset >= 13:
        result.graph.initializer.append(numpy_helper.from_array(np.array([-1], dtype=np.int64), axes))
    if opset >= 18:
        nodes.append(helper.make_node("ReduceMax", [source, axes], [score], keepdims=0))
    else:
        nodes.append(helper.make_node("ReduceMax", [source], [score], axes=[-1], keepdims=0))
    for value in (index + "_float", score):
        if opset >= 13:
            nodes.append(helper.make_node("Unsqueeze", [value, axes], [value + "_expanded"]))
        else:
            nodes.append(helper.make_node("Unsqueeze", [value], [value + "_expanded"], axes=[-1]))
    nodes.append(helper.make_node("Concat", [index + "_float_expanded", score + "_expanded"], [PREFIX + "packed"], axis=-1))
    result.graph.node.extend(nodes)
    del result.graph.output[:]
    result.graph.output.append(helper.make_tensor_value_info(PREFIX + "packed", TensorProto.FLOAT, [1, None, 2]))
    onnx.checker.check_model(result)
    return result


def decode(indices: np.ndarray, scores: np.ndarray, dictionary: list[str]) -> tuple[str, float, int]:
    emitted = [time for time, value in enumerate(indices) if value != 0 and (time == 0 or value != indices[time - 1])]
    text = "".join(dictionary[int(indices[time]) - 1] for time in emitted)
    confidence = sum(float(scores[time]) for time in emitted) / len(emitted) if emitted else 0.0
    return text, confidence, len(emitted)


def compare(name: str, full: np.ndarray, packed: np.ndarray, dictionary: list[str], replay: list[dict]) -> dict:
    check(full.dtype == np.float32 and full.ndim == 3 and full.shape[0] == 1 and full.shape[2] == len(dictionary) + 1,
          "Full probability witness violates [1,time,dictionary+blank] float32 ABI")
    check(packed.dtype == np.float32 and packed.shape == (1, full.shape[1], 2), "Packed output violates [1,time,2] ABI")
    check(np.isfinite(full).all() and np.logical_and(full >= 0, full <= 1).all(), "Full matrix has non-probability values")
    check(np.isfinite(packed).all(), "Packed output contains non-finite values")
    expected_indices = full.argmax(axis=-1)[0]
    expected_scores = full.max(axis=-1)[0]
    indices_equal = np.array_equal(packed[0, :, 0], expected_indices.astype(np.float32))
    score_error = float(np.max(np.abs(packed[0, :, 1].astype(np.float64) - expected_scores)))
    check(indices_equal and score_error <= TOLERANCE, "Full-versus-packed numerical parity failed")
    expected_text, expected_confidence, expected_count = decode(expected_indices, expected_scores, dictionary)
    actual_text, actual_confidence, actual_count = decode(packed[0, :, 0].astype(np.int64), packed[0, :, 1], dictionary)
    check(actual_text == expected_text and actual_count == expected_count and abs(actual_confidence - expected_confidence) <= TOLERANCE,
          "CTC decode parity failed")
    replay.append({"name": name, "probability_shape": list(full.shape), "packed_shape": list(packed.shape),
                   "packed_values": packed.reshape(-1).tolist(), "expected_indices": expected_indices.tolist(),
                   "expected_text": expected_text, "expected_confidence": expected_confidence,
                   "expected_token_count": expected_count})
    return {"name": name, "input_fixture_is_synthetic": True, "probability_shape": list(full.shape),
            "packed_shape": list(packed.shape), "argmax_exact": indices_equal, "maximum_score_absolute_error": score_error,
            "ctc_text_exact": actual_text == expected_text, "emitted_tokens": expected_count,
            "probability_row_sum_max_error": float(np.max(np.abs(full.sum(axis=-1, dtype=np.float64) - 1))),
            "full_output_bytes": full.nbytes, "packed_output_bytes": packed.nbytes,
            "decoded_text_sha256": hashlib.sha256(expected_text.encode()).hexdigest()}


# Small deterministic bitmap patterns are generated in code; no OS font files or captured images are used.
GLYPHS = {
    " ": [0, 0, 0, 0, 0, 0, 0], "K": [17, 18, 20, 24, 20, 18, 17],
    "E": [31, 16, 16, 30, 16, 16, 31], "P": [30, 17, 17, 30, 16, 16, 16],
    "O": [14, 17, 17, 17, 17, 17, 14], "F": [31, 16, 16, 30, 16, 16, 16],
    "M": [17, 27, 21, 21, 17, 17, 17], "R": [30, 17, 17, 30, 20, 18, 17],
    "G": [14, 17, 16, 23, 17, 17, 15], "N": [17, 25, 25, 21, 19, 19, 17],
    "C": [14, 17, 16, 16, 16, 17, 14], "Y": [17, 17, 10, 4, 4, 4, 4],
    "X": [17, 17, 10, 4, 10, 17, 17], "I": [31, 4, 4, 4, 4, 4, 31],
    "T": [31, 4, 4, 4, 4, 4, 4],
}


def text_tensor(text: str, width: int) -> np.ndarray:
    image = np.ones((48, width), dtype=np.float32)
    scale = max(1, min(4, (width - 8) // (len(text) * 6)))
    for letter, character in enumerate(text):
        for row, bits in enumerate(GLYPHS[character]):
            for column in range(5):
                if bits & (1 << (4 - column)):
                    x, y = 4 + (letter * 6 + column) * scale, 6 + row * scale
                    image[y:y + scale, x:x + scale] = -1
    return np.repeat(image[None, None, :, :], 3, axis=1)


def execute(args: argparse.Namespace, report: dict) -> dict:
    lock = json.loads((ROOT / "models/models.lock.json").read_text())
    candidate = next(item for item in lock["candidates"] if item["id"] == "pp-ocrv6-tiny-rec")
    raw = args.model.read_bytes()
    check(len(raw) == candidate["artifact_size_bytes"] and hashlib.sha256(raw).hexdigest() == candidate["artifact_sha256"], "Model identity differs from lock")
    dictionary_raw = args.dictionary.read_bytes()
    contract = candidate["recognition_dictionary"]
    check(hashlib.sha256(dictionary_raw).hexdigest() == contract["generated_artifact_sha256"], "Dictionary identity differs from lock")
    dictionary = dictionary_raw.decode("utf-8").removesuffix("\n").split("\n")
    check(len(dictionary) == contract["raw_token_count"], "Unexpected raw dictionary length")
    if contract["use_space_char"]:
        dictionary.append(" ")
    check(len(dictionary) == contract["effective_token_count"], "Unexpected effective dictionary length")
    class_count = len(dictionary) + 1
    model = onnx.load_model_from_string(raw)
    onnx.checker.check_model(model)
    full_session = session(model)
    packed_session = session(pack_graph(model))
    report["phase"] = "real-model-inference"
    inputs = full_session.get_inputs()
    check(len(inputs) == 1 and inputs[0].type == "tensor(float)", "Expected one float32 recognizer input")
    shape = inputs[0].shape
    check(len(shape) == 4 and shape[1] in (3, None, "3") and shape[2] == 48, "Recognizer input is not the reviewed NCHW/3/48 ABI")
    check(not isinstance(shape[0], int) or shape[0] == 1, "Static model batch is not one")
    widths = [shape[3]] if isinstance(shape[3], int) and shape[3] > 0 else [64, 160, 320, 480]
    rng = np.random.Generator(np.random.PCG64(20260908))
    replay: list[dict] = []
    rows = []
    for width in widths:
        fixtures = [("constant_black", np.full((1, 3, 48, width), -1, dtype=np.float32)),
                    ("constant_gray", np.zeros((1, 3, 48, width), dtype=np.float32)),
                    ("constant_white", np.ones((1, 3, 48, width), dtype=np.float32)),
                    ("seeded_noise", rng.uniform(-1, 1, size=(1, 3, 48, width)).astype(np.float32)),
                    ("bitmap_keep_off", text_tensor("KEEP OFF", width)),
                    ("bitmap_emergency_exit", text_tensor("EMERGENCY EXIT", width))]
        for fixture_name, tensor in fixtures:
            full = full_session.run(None, {inputs[0].name: tensor})[0]
            packed = packed_session.run(None, {inputs[0].name: tensor})[0]
            rows.append(compare(f"real:{fixture_name}:width={width}", full, packed, dictionary, replay))
    report["real_model_cases"] = len(rows)
    report["phase"] = "synthetic-tie-and-ctc-inference"
    synthetic_graph = helper.make_graph([helper.make_node("Identity", ["input"], ["output"])], "synthetic-probabilities",
        [helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, None, class_count])],
        [helper.make_tensor_value_info("output", TensorProto.FLOAT, [1, None, class_count])])
    synthetic_model = helper.make_model(synthetic_graph, opset_imports=[helper.make_opsetid("", 18)], ir_version=8)
    synthetic_session = session(pack_graph(synthetic_model))
    for time_steps in (8, 17, 64):
        probabilities = np.zeros((1, time_steps, class_count), dtype=np.float32)
        winners = [0, 1, 1, 0, 1, class_count - 1, class_count - 1, 0]
        for time in range(time_steps):
            probabilities[0, time, winners[time % len(winners)]] = 1
        # Include a nonblank tie and a blank/nonblank tie. select_last_index=0 is mandatory.
        probabilities[0, 0, :] = 0; probabilities[0, 0, 0] = probabilities[0, 0, class_count - 1] = 0.5
        probabilities[0, 1, :] = 0; probabilities[0, 1, 1] = probabilities[0, 1, class_count - 1] = 0.5
        packed = synthetic_session.run(None, {"input": probabilities})[0]
        rows.append(compare(f"synthetic:blank-repeat-ties:time={time_steps}", probabilities, packed, dictionary, replay))
        uniform = np.full_like(probabilities, np.float32(1 / class_count))
        rows.append(compare(f"synthetic:all-class-tie:time={time_steps}", uniform,
                            synthetic_session.run(None, {"input": uniform})[0], dictionary, replay))
    args.replay.parent.mkdir(parents=True, exist_ok=True)
    args.replay.write_text(json.dumps({"dictionary": dictionary, "real_model_cases": report["real_model_cases"], "cases": replay}, ensure_ascii=False, allow_nan=False))
    return {**report, "phase": "completed", "status": "pass", "safety_result": "PASS", "rows": rows,
            "synthetic_probability_cases": len(rows) - report["real_model_cases"],
            "model_revision": candidate["revision"], "model_sha256": hashlib.sha256(raw).hexdigest(),
            "dictionary_sha256": hashlib.sha256(dictionary_raw).hexdigest(), "class_count": class_count,
            "onnx_ir_version": model.ir_version, "onnx_opsets": {item.domain or "ai.onnx": item.version for item in model.opset_import},
            "model_input_shape": shape, "tested_widths": widths, "provider": full_session.get_providers(),
            "replay_sha256": hashlib.sha256(args.replay.read_bytes()).hexdigest(),
            "csharp_core_replay_performed": False}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--dictionary", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--replay", type=Path, required=True)
    args = parser.parse_args()
    report = {"experiment": "pinned-ppocr-onnx-cpu-full-vs-packed", "status": "uncertain", "phase": "preflight",
              "real_model_cases": 0, "seed": 20260908, "scope": "actual-pinned-ONNX-model-on-host-CPU",
              "git_commit": os.getenv("GITHUB_SHA"), "python": platform.python_version(), "architecture": platform.machine(),
              "versions": {name: importlib.metadata.version(name) for name in ("numpy", "onnx", "onnxruntime", "protobuf")},
              "batch": 1, "intra_op_threads": 1, "inter_op_threads": 1, "score_absolute_tolerance": TOLERANCE,
              "real_unity_execution_performed": False, "gpu_execution_performed": False, "quest_execution_performed": False,
              "texture_preprocessing_parity_measured": False, "natural_image_ocr_accuracy_measured": False,
              "latency_measured": False, "model_bytes_in_artifact": False}
    try:
        report = execute(args, report)
        result = 0
    except Exception as error:
        report.update(status="failed", safety_result="UNCERTAIN" if report["real_model_cases"] == 0 else "FAIL", error_type=type(error).__name__, error=str(error))
        result = 1
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False) + "\n")
    print(json.dumps(report, ensure_ascii=False, allow_nan=False))
    return result


if __name__ == "__main__":
    raise SystemExit(main())
