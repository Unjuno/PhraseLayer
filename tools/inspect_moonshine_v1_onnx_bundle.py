#!/usr/bin/env python3
"""Inspect a Moonshine v1 four-graph ONNX bundle and emit small ABI evidence.

No model files are bundled by this repository. Validation is dependency-free when called
with synthetic graph dictionaries; inspecting real ONNX files requires the optional `onnx`
package in the probe/export environment.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
from typing import Any, Dict, Mapping

EXPECTED_FILES = (
    "preprocess.onnx",
    "encode.onnx",
    "uncached_decode.onnx",
    "cached_decode.onnx",
)
CACHE_STATE_COUNT = 24
HIDDEN_SIZE = 288
VOCAB_SIZE = 32768


class ContractError(ValueError):
    pass


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise ContractError(message)


def _tensors(graph: Mapping[str, Any], direction: str) -> list[Mapping[str, Any]]:
    value = graph.get(direction)
    _require(isinstance(value, list), f"{graph.get('name', '<graph>')}: {direction} must be a list")
    for tensor in value:
        _require(isinstance(tensor, Mapping), f"{graph.get('name', '<graph>')}: invalid tensor entry")
    return value


def _require_tensor(
    graph: Mapping[str, Any],
    direction: str,
    index: int,
    dtype: str,
    rank: int,
    fixed_dims: Mapping[int, int] | None = None,
) -> Mapping[str, Any]:
    tensors = _tensors(graph, direction)
    _require(index < len(tensors), f"{graph['name']}: missing {direction[:-1]} index {index}")
    tensor = tensors[index]
    actual_dtype = tensor.get("dtype")
    _require(
        actual_dtype == dtype,
        f"{graph['name']}: {direction[:-1]}[{index}] dtype expected {dtype} but found {actual_dtype}",
    )
    shape = tensor.get("shape")
    _require(
        isinstance(shape, list) and len(shape) == rank,
        f"{graph['name']}: {direction[:-1]}[{index}] rank expected {rank} but found {shape!r}",
    )
    for axis, expected in (fixed_dims or {}).items():
        value = shape[axis]
        if isinstance(value, int):
            _require(
                value == expected,
                f"{graph['name']}: {direction[:-1]}[{index}] axis {axis} expected {expected} but found {value}",
            )
    return tensor


def _require_count(graph: Mapping[str, Any], inputs: int, outputs: int) -> None:
    actual_inputs = len(_tensors(graph, "inputs"))
    actual_outputs = len(_tensors(graph, "outputs"))
    _require(
        actual_inputs == inputs and actual_outputs == outputs,
        f"{graph['name']}: ABI expected {inputs} inputs/{outputs} outputs but found {actual_inputs}/{actual_outputs}",
    )


def validate_bundle_manifest(bundle: Mapping[str, Mapping[str, Any]]) -> Dict[str, Any]:
    missing = [name for name in EXPECTED_FILES if name not in bundle]
    _require(not missing, "missing Moonshine v1 ONNX graphs: " + ", ".join(missing))

    preprocess = bundle["preprocess.onnx"]
    encoder = bundle["encode.onnx"]
    uncached = bundle["uncached_decode.onnx"]
    cached = bundle["cached_decode.onnx"]

    _require_count(preprocess, 1, 1)
    _require_tensor(preprocess, "inputs", 0, "FLOAT", 2)
    _require_tensor(preprocess, "outputs", 0, "FLOAT", 3, {2: HIDDEN_SIZE})

    _require_count(encoder, 2, 1)
    _require_tensor(encoder, "inputs", 0, "FLOAT", 3, {2: HIDDEN_SIZE})
    _require_tensor(encoder, "inputs", 1, "INT32", 1)
    _require_tensor(encoder, "outputs", 0, "FLOAT", 3, {2: HIDDEN_SIZE})

    _require_count(uncached, 3, 1 + CACHE_STATE_COUNT)
    _require_tensor(uncached, "inputs", 0, "INT32", 2)
    _require_tensor(uncached, "inputs", 1, "FLOAT", 3, {2: HIDDEN_SIZE})
    _require_tensor(uncached, "inputs", 2, "INT32", 1)
    _require_tensor(uncached, "outputs", 0, "FLOAT", 3, {2: VOCAB_SIZE})
    for index in range(CACHE_STATE_COUNT):
        _require_tensor(uncached, "outputs", 1 + index, "FLOAT", 4)

    _require_count(cached, 3 + CACHE_STATE_COUNT, 1 + CACHE_STATE_COUNT)
    _require_tensor(cached, "inputs", 0, "INT32", 2)
    _require_tensor(cached, "inputs", 1, "FLOAT", 3, {2: HIDDEN_SIZE})
    _require_tensor(cached, "inputs", 2, "INT32", 1)
    for index in range(CACHE_STATE_COUNT):
        _require_tensor(cached, "inputs", 3 + index, "FLOAT", 4)
    _require_tensor(cached, "outputs", 0, "FLOAT", 3, {2: VOCAB_SIZE})
    for index in range(CACHE_STATE_COUNT):
        _require_tensor(cached, "outputs", 1 + index, "FLOAT", 4)

    return {
        "bundle_kind": "moonshine-v1-four-graph",
        "cache_state_count": CACHE_STATE_COUNT,
        "hidden_size": HIDDEN_SIZE,
        "vocabulary_size": VOCAB_SIZE,
        "binding": "positional",
    }


def _dimension_value(dimension: Any) -> int | str | None:
    if getattr(dimension, "dim_value", 0):
        return int(dimension.dim_value)
    if getattr(dimension, "dim_param", ""):
        return str(dimension.dim_param)
    return None


def _tensor_from_value_info(value_info: Any, onnx_module: Any) -> Dict[str, Any]:
    tensor_type = value_info.type.tensor_type
    return {
        "name": value_info.name,
        "dtype": onnx_module.TensorProto.DataType.Name(tensor_type.elem_type),
        "shape": [_dimension_value(dim) for dim in tensor_type.shape.dim],
    }


def inspect_graph(path: pathlib.Path) -> Dict[str, Any]:
    try:
        import onnx  # type: ignore
    except ImportError as exc:
        raise RuntimeError("Real Moonshine ONNX inspection requires the optional 'onnx' package") from exc

    model = onnx.load(str(path), load_external_data=False)
    initializer_names = {initializer.name for initializer in model.graph.initializer}
    data = path.read_bytes()
    return {
        "name": path.name,
        "size_bytes": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        "inputs": [
            _tensor_from_value_info(value, onnx)
            for value in model.graph.input
            if value.name not in initializer_names
        ],
        "outputs": [_tensor_from_value_info(value, onnx) for value in model.graph.output],
    }


def inspect_bundle(bundle_dir: pathlib.Path) -> Dict[str, Any]:
    graphs: Dict[str, Any] = {}
    for name in EXPECTED_FILES:
        path = bundle_dir / name
        if not path.is_file():
            raise ContractError(f"missing Moonshine v1 ONNX file: {path}")
        graphs[name] = inspect_graph(path)
    contract = validate_bundle_manifest(graphs)
    return {"schema_version": 1, "contract": contract, "graphs": graphs}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle-dir", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path)
    args = parser.parse_args()

    report = inspect_bundle(args.bundle_dir)
    rendered = json.dumps(report, indent=2, sort_keys=True) + "\n"
    if args.output is not None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
