#!/usr/bin/env python3
"""Inspect and validate a local Optimum Marian ONNX export without bundling weights.

The runtime repository keeps model weights out of git. This tool is intended to run against a local
revision-pinned export directory and emits a small JSON evidence manifest containing graph signatures,
file sizes, and SHA-256 hashes.

The validation logic itself is dependency-free and is exercised by synthetic fixtures. Loading real ONNX
files requires the optional `onnx` Python package.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
from typing import Any, Dict, Iterable, List, Mapping, MutableMapping, Sequence

EXPECTED_FILES = (
    "encoder_model.onnx",
    "decoder_model.onnx",
    "decoder_with_past_model.onnx",
)
EXPECTED_LAYERS = 6
EXPECTED_MODEL_DIM = 512
EXPECTED_ATTENTION_HEADS = 8
EXPECTED_HEAD_DIM = 64
EXPECTED_VOCAB_SIZE = 46276

CACHE_RE = re.compile(r"^(?:past_key_values|present)\.(\d+)\.(decoder|encoder)\.(key|value)$")


class ContractError(ValueError):
    pass


def _index_tensors(graph: Mapping[str, Any], direction: str) -> Dict[str, Mapping[str, Any]]:
    tensors = graph.get(direction)
    if not isinstance(tensors, list):
        raise ContractError(f"{graph.get('name', '<graph>')}: {direction} must be a list")
    indexed: Dict[str, Mapping[str, Any]] = {}
    for tensor in tensors:
        if not isinstance(tensor, Mapping):
            raise ContractError(f"{graph.get('name', '<graph>')}: invalid {direction} tensor entry")
        name = tensor.get("name")
        if not isinstance(name, str) or not name:
            raise ContractError(f"{graph.get('name', '<graph>')}: tensor name is missing")
        if name in indexed:
            raise ContractError(f"{graph.get('name', '<graph>')}: duplicate tensor {name}")
        indexed[name] = tensor
    return indexed


def _require_tensor(
    graph: Mapping[str, Any],
    direction: str,
    name: str,
    dtype: str,
    rank: int,
    fixed_dims: Mapping[int, int] | None = None,
) -> Mapping[str, Any]:
    indexed = _index_tensors(graph, direction)
    if name not in indexed:
        raise ContractError(f"{graph['name']}: missing required {direction[:-1]} {name}")
    tensor = indexed[name]
    actual_dtype = tensor.get("dtype")
    if actual_dtype != dtype:
        raise ContractError(
            f"{graph['name']}: {name} dtype expected {dtype} but found {actual_dtype}"
        )
    shape = tensor.get("shape")
    if not isinstance(shape, list) or len(shape) != rank:
        raise ContractError(
            f"{graph['name']}: {name} rank expected {rank} but found {shape!r}"
        )
    for axis, expected in (fixed_dims or {}).items():
        value = shape[axis]
        if isinstance(value, int) and value != expected:
            raise ContractError(
                f"{graph['name']}: {name} axis {axis} expected {expected} but found {value}"
            )
    return tensor


def _cache_name(prefix: str, layer: int, attention: str, item: str) -> str:
    return f"{prefix}.{layer}.{attention}.{item}"


def _require_cache(
    graph: Mapping[str, Any],
    direction: str,
    prefix: str,
    layer: int,
    attention: str,
    item: str,
) -> None:
    _require_tensor(
        graph,
        direction,
        _cache_name(prefix, layer, attention, item),
        "FLOAT",
        4,
        {1: EXPECTED_ATTENTION_HEADS, 3: EXPECTED_HEAD_DIM},
    )


def _validate_cache_layer_range(graph: Mapping[str, Any]) -> None:
    for direction in ("inputs", "outputs"):
        for name in _index_tensors(graph, direction):
            match = CACHE_RE.match(name)
            if not match:
                continue
            layer = int(match.group(1))
            if layer < 0 or layer >= EXPECTED_LAYERS:
                raise ContractError(
                    f"{graph['name']}: cache tensor {name} references unexpected layer {layer}"
                )


def validate_bundle_manifest(bundle: Mapping[str, Mapping[str, Any]]) -> Dict[str, Any]:
    missing = [name for name in EXPECTED_FILES if name not in bundle]
    if missing:
        raise ContractError("missing Marian ONNX graphs: " + ", ".join(missing))

    encoder = bundle["encoder_model.onnx"]
    decoder = bundle["decoder_model.onnx"]
    decoder_with_past = bundle["decoder_with_past_model.onnx"]

    _require_tensor(encoder, "inputs", "input_ids", "INT64", 2)
    _require_tensor(encoder, "inputs", "attention_mask", "INT64", 2)
    _require_tensor(
        encoder,
        "outputs",
        "last_hidden_state",
        "FLOAT",
        3,
        {2: EXPECTED_MODEL_DIM},
    )

    for graph in (decoder, decoder_with_past):
        _require_tensor(graph, "inputs", "input_ids", "INT64", 2)
        _require_tensor(
            graph,
            "inputs",
            "encoder_hidden_states",
            "FLOAT",
            3,
            {2: EXPECTED_MODEL_DIM},
        )
        _require_tensor(graph, "inputs", "encoder_attention_mask", "INT64", 2)
        _require_tensor(
            graph,
            "outputs",
            "logits",
            "FLOAT",
            3,
            {2: EXPECTED_VOCAB_SIZE},
        )

    for layer in range(EXPECTED_LAYERS):
        for attention in ("decoder", "encoder"):
            for item in ("key", "value"):
                _require_cache(decoder, "outputs", "present", layer, attention, item)
                _require_cache(
                    decoder_with_past,
                    "inputs",
                    "past_key_values",
                    layer,
                    attention,
                    item,
                )
        for item in ("key", "value"):
            _require_cache(decoder_with_past, "outputs", "present", layer, "decoder", item)

    with_past_outputs = _index_tensors(decoder_with_past, "outputs")
    cross_presence = []
    for layer in range(EXPECTED_LAYERS):
        for item in ("key", "value"):
            cross_presence.append(
                _cache_name("present", layer, "encoder", item) in with_past_outputs
            )
    if any(cross_presence) and not all(cross_presence):
        raise ContractError(
            "decoder_with_past_model.onnx: encoder present-cache outputs must be all-or-none"
        )
    if all(cross_presence):
        for layer in range(EXPECTED_LAYERS):
            for item in ("key", "value"):
                _require_cache(
                    decoder_with_past,
                    "outputs",
                    "present",
                    layer,
                    "encoder",
                    item,
                )

    for graph in (encoder, decoder, decoder_with_past):
        _validate_cache_layer_range(graph)

    return {
        "decoder_with_past_returns_cross_attention_cache": all(cross_presence),
        "decoder_layers": EXPECTED_LAYERS,
        "attention_heads": EXPECTED_ATTENTION_HEADS,
        "head_dimension": EXPECTED_HEAD_DIM,
        "model_dimension": EXPECTED_MODEL_DIM,
        "vocabulary_size": EXPECTED_VOCAB_SIZE,
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
        raise RuntimeError(
            "Real ONNX inspection requires the optional 'onnx' package. "
            "Install a reviewed/pinned tooling version in the export environment."
        ) from exc

    model = onnx.load(str(path), load_external_data=False)
    initializer_names = {initializer.name for initializer in model.graph.initializer}
    inputs = [
        _tensor_from_value_info(value, onnx)
        for value in model.graph.input
        if value.name not in initializer_names
    ]
    outputs = [_tensor_from_value_info(value, onnx) for value in model.graph.output]
    data = path.read_bytes()
    return {
        "name": path.name,
        "size_bytes": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        "inputs": inputs,
        "outputs": outputs,
    }


def inspect_bundle(bundle_dir: pathlib.Path) -> Dict[str, Any]:
    graphs: Dict[str, Any] = {}
    for name in EXPECTED_FILES:
        path = bundle_dir / name
        if not path.is_file():
            raise FileNotFoundError(f"missing Marian ONNX artifact: {path}")
        graphs[name] = inspect_graph(path)
    contract = validate_bundle_manifest(graphs)
    return {"schema_version": 1, "contract": contract, "graphs": graphs}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle-dir", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()

    manifest = inspect_bundle(args.bundle_dir)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(json.dumps(manifest["contract"], sort_keys=True))


if __name__ == "__main__":
    main()
