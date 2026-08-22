#!/usr/bin/env python3
"""Reproducible ONNX export + parity probe for the pinned OPUS-MT candidate.

The probe intentionally does not publish model weights. It exports to the ephemeral runner and uploads only
metadata: exact file names/sizes/hashes, ONNX graph signatures, tokenizer fixtures, toolchain versions, and
reference-vs-ORT parity.
"""

from __future__ import annotations

import argparse
import gc
import hashlib
import importlib.metadata
import json
import platform
import shutil
import sys
import traceback
from collections import Counter
from pathlib import Path
from typing import Any

MODEL_ID = "Helsinki-NLP/opus-mt-en-jap"
REVISION = "a863894cdd2b80f3bc1c5966734aee9ffec207d1"
TASK = "text2text-generation-with-past"
SAMPLES = [
    "I was tired, so I went home.",
    "Please keep off the grass.",
]
GENERATION = {
    "num_beams": 4,
    "max_new_tokens": 64,
    "renormalize_logits": True,
}


def package_version(name: str) -> str:
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return "<not-installed>"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while True:
            chunk = handle.read(1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def tensor_shape(value_info: Any) -> list[Any]:
    tensor = value_info.type.tensor_type
    if not tensor.HasField("shape"):
        return []
    dimensions: list[Any] = []
    for dimension in tensor.shape.dim:
        if dimension.HasField("dim_value"):
            dimensions.append(int(dimension.dim_value))
        elif dimension.HasField("dim_param"):
            dimensions.append(str(dimension.dim_param))
        else:
            dimensions.append("?")
    return dimensions


def inspect_onnx(path: Path) -> dict[str, Any]:
    import onnx

    model = onnx.load_model(str(path), load_external_data=False)
    operator_counts = Counter(node.op_type for node in model.graph.node)
    external_locations: set[str] = set()
    for initializer in model.graph.initializer:
        if initializer.data_location != onnx.TensorProto.EXTERNAL:
            continue
        for item in initializer.external_data:
            if item.key == "location" and item.value:
                external_locations.add(item.value)

    return {
        "ir_version": int(model.ir_version),
        "opsets": [
            {"domain": item.domain or "ai.onnx", "version": int(item.version)}
            for item in model.opset_import
        ],
        "inputs": [
            {
                "name": item.name,
                "element_type": int(item.type.tensor_type.elem_type),
                "shape": tensor_shape(item),
            }
            for item in model.graph.input
        ],
        "outputs": [
            {
                "name": item.name,
                "element_type": int(item.type.tensor_type.elem_type),
                "shape": tensor_shape(item),
            }
            for item in model.graph.output
        ],
        "node_count": len(model.graph.node),
        "operator_counts": dict(sorted(operator_counts.items())),
        "external_data_locations": sorted(external_locations),
    }


def collect_files(output: Path) -> list[dict[str, Any]]:
    files: list[dict[str, Any]] = []
    for path in sorted(item for item in output.rglob("*") if item.is_file()):
        relative = path.relative_to(output).as_posix()
        item: dict[str, Any] = {
            "path": relative,
            "size_bytes": path.stat().st_size,
            "sha256": sha256_file(path),
        }
        if path.suffix.lower() == ".onnx":
            item["onnx"] = inspect_onnx(path)
        files.append(item)
    return files


def runtime_sets(files: list[dict[str, Any]]) -> dict[str, Any]:
    by_path = {item["path"]: item for item in files}

    def describe(paths: list[str]) -> dict[str, Any]:
        missing = [path for path in paths if path not in by_path]
        if missing:
            return {"files": paths, "missing": missing, "total_size_bytes": None}
        total = sum(int(by_path[path]["size_bytes"]) for path in paths)
        return {
            "files": paths,
            "total_size_bytes": total,
            "total_size_mib": round(total / (1024 * 1024), 3),
        }

    return {
        "encoder_plus_decoder": describe([
            "encoder_model.onnx",
            "decoder_model.onnx",
        ]),
        "encoder_plus_merged_decoder": describe([
            "encoder_model.onnx",
            "decoder_model_merged.onnx",
        ]),
        "encoder_plus_split_decoder_cache": describe([
            "encoder_model.onnx",
            "decoder_model.onnx",
            "decoder_with_past_model.onnx",
        ]),
        "all_onnx_outputs": describe([
            item["path"] for item in files if item["path"].endswith(".onnx")
        ]),
    }


def generation_result(tokenizer: Any, generated: Any) -> list[dict[str, Any]]:
    token_rows = generated.detach().cpu().tolist()
    translations = tokenizer.batch_decode(generated, skip_special_tokens=True)
    return [
        {
            "source": source,
            "token_ids": [int(token) for token in token_ids],
            "translation": translation,
        }
        for source, token_ids, translation in zip(SAMPLES, token_rows, translations)
    ]


def tokenizer_fixture(tokenizer: Any) -> dict[str, Any]:
    samples: list[dict[str, Any]] = []
    for source in SAMPLES:
        encoded = tokenizer(
            source,
            add_special_tokens=True,
            return_attention_mask=True,
            padding=False,
            truncation=False,
        )
        input_ids = [int(value) for value in encoded["input_ids"]]
        attention_mask = [int(value) for value in encoded["attention_mask"]]
        samples.append({
            "source": source,
            "input_ids": input_ids,
            "attention_mask": attention_mask,
            "decoded_skip_special_tokens": tokenizer.decode(input_ids, skip_special_tokens=True),
            "tokens": [str(value) for value in tokenizer.convert_ids_to_tokens(input_ids)],
        })

    return {
        "tokenizer_class": type(tokenizer).__name__,
        "vocab_size": int(tokenizer.vocab_size),
        "pad_token": tokenizer.pad_token,
        "pad_token_id": None if tokenizer.pad_token_id is None else int(tokenizer.pad_token_id),
        "eos_token": tokenizer.eos_token,
        "eos_token_id": None if tokenizer.eos_token_id is None else int(tokenizer.eos_token_id),
        "bos_token": tokenizer.bos_token,
        "bos_token_id": None if tokenizer.bos_token_id is None else int(tokenizer.bos_token_id),
        "unk_token": tokenizer.unk_token,
        "unk_token_id": None if tokenizer.unk_token_id is None else int(tokenizer.unk_token_id),
        "samples": samples,
    }


def run_reference() -> tuple[list[dict[str, Any]], dict[str, Any]]:
    import torch
    from transformers import AutoModelForSeq2SeqLM, AutoTokenizer

    tokenizer = AutoTokenizer.from_pretrained(MODEL_ID, revision=REVISION, trust_remote_code=False)
    model = AutoModelForSeq2SeqLM.from_pretrained(MODEL_ID, revision=REVISION, trust_remote_code=False)
    model.eval()
    tokenizer_reference = tokenizer_fixture(tokenizer)
    encoded = tokenizer(SAMPLES, return_tensors="pt", padding=True)
    with torch.no_grad():
        generated = model.generate(**encoded, **GENERATION)
    result = generation_result(tokenizer, generated)
    del generated, encoded, model, tokenizer
    gc.collect()
    return result, tokenizer_reference


def run_onnx(output: Path) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    from optimum.onnxruntime import ORTModelForSeq2SeqLM
    from transformers import AutoTokenizer

    tokenizer = AutoTokenizer.from_pretrained(str(output), local_files_only=True, trust_remote_code=False)
    model = ORTModelForSeq2SeqLM.from_pretrained(
        str(output),
        provider="CPUExecutionProvider",
        local_files_only=True,
    )
    tokenizer_exported = tokenizer_fixture(tokenizer)
    encoded = tokenizer(SAMPLES, return_tensors="pt", padding=True)
    generated = model.generate(**encoded, **GENERATION)
    result = generation_result(tokenizer, generated)
    del generated, encoded, model, tokenizer
    gc.collect()
    return result, tokenizer_exported


def compare_parity(reference: list[dict[str, Any]], onnx: list[dict[str, Any]]) -> dict[str, Any]:
    comparisons: list[dict[str, Any]] = []
    for reference_item, onnx_item in zip(reference, onnx):
        comparisons.append({
            "source": reference_item["source"],
            "token_ids_equal": reference_item["token_ids"] == onnx_item["token_ids"],
            "text_equal": reference_item["translation"] == onnx_item["translation"],
            "reference_translation": reference_item["translation"],
            "onnx_translation": onnx_item["translation"],
            "reference_token_ids": reference_item["token_ids"],
            "onnx_token_ids": onnx_item["token_ids"],
        })
    exact = (
        len(reference) == len(onnx)
        and len(comparisons) == len(SAMPLES)
        and all(item["token_ids_equal"] and item["text_equal"] for item in comparisons)
    )
    return {"exact": exact, "samples": comparisons}


def compare_tokenizer_parity(reference: dict[str, Any], exported: dict[str, Any]) -> dict[str, Any]:
    metadata_keys = (
        "tokenizer_class",
        "vocab_size",
        "pad_token",
        "pad_token_id",
        "eos_token",
        "eos_token_id",
        "bos_token",
        "bos_token_id",
        "unk_token",
        "unk_token_id",
    )
    metadata_equal = all(reference.get(key) == exported.get(key) for key in metadata_keys)
    reference_samples = reference.get("samples", [])
    exported_samples = exported.get("samples", [])
    comparisons: list[dict[str, Any]] = []
    for reference_item, exported_item in zip(reference_samples, exported_samples):
        comparisons.append({
            "source": reference_item["source"],
            "input_ids_equal": reference_item["input_ids"] == exported_item["input_ids"],
            "attention_mask_equal": reference_item["attention_mask"] == exported_item["attention_mask"],
            "tokens_equal": reference_item["tokens"] == exported_item["tokens"],
            "decoded_equal": reference_item["decoded_skip_special_tokens"] == exported_item["decoded_skip_special_tokens"],
        })
    exact = (
        metadata_equal
        and len(reference_samples) == len(exported_samples) == len(SAMPLES)
        and all(
            item["input_ids_equal"]
            and item["attention_mask_equal"]
            and item["tokens_equal"]
            and item["decoded_equal"]
            for item in comparisons
        )
    )
    return {
        "exact": exact,
        "metadata_equal": metadata_equal,
        "samples": comparisons,
    }


def write_report(path: Path, report: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()

    report: dict[str, Any] = {
        "schema_version": 4,
        "model_id": MODEL_ID,
        "revision": REVISION,
        "task": TASK,
        "generation": GENERATION,
        "runtime_status": "unverified-real-unity-import-required",
        "environment": {
            "python": sys.version,
            "platform": platform.platform(),
            "torch": package_version("torch"),
            "transformers": package_version("transformers"),
            "optimum": package_version("optimum"),
            "optimum-onnx": package_version("optimum-onnx"),
            "onnx": package_version("onnx"),
            "onnxruntime": package_version("onnxruntime"),
            "sentencepiece": package_version("sentencepiece"),
        },
        "status": "started",
    }

    try:
        from optimum.exporters.onnx import main_export

        reference, tokenizer_reference = run_reference()
        report["reference_samples"] = reference
        report["tokenizer_reference"] = tokenizer_reference
        write_report(args.report, report)

        if args.output.exists():
            shutil.rmtree(args.output)
        args.output.mkdir(parents=True)

        main_export(
            MODEL_ID,
            output=args.output,
            task=TASK,
            revision=REVISION,
            framework="pt",
            monolith=False,
            trust_remote_code=False,
            do_validation=True,
        )

        files = collect_files(args.output)
        report["files"] = files
        report["runtime_sets"] = runtime_sets(files)
        onnx, tokenizer_exported = run_onnx(args.output)
        report["onnx_samples"] = onnx
        report["tokenizer_exported"] = tokenizer_exported
        report["tokenizer_parity"] = compare_tokenizer_parity(tokenizer_reference, tokenizer_exported)
        report["parity"] = compare_parity(reference, onnx)
        if not report["tokenizer_parity"]["exact"]:
            raise RuntimeError("Pinned source and exported tokenizer files are not token-exact on the probe samples.")
        if not report["parity"]["exact"]:
            raise RuntimeError("Pinned PyTorch and exported ONNX generation are not token-exact on the probe samples.")

        report["status"] = "pass"
        write_report(args.report, report)
        return 0
    except Exception as exception:  # noqa: BLE001 - probe must preserve failure details in report
        report["status"] = "fail"
        report["error_type"] = type(exception).__name__
        report["error"] = str(exception)
        report["traceback"] = traceback.format_exc()
        write_report(args.report, report)
        raise


if __name__ == "__main__":
    raise SystemExit(main())
