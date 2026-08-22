#!/usr/bin/env python3
"""One-shot, non-production ONNX export probe for the pinned OPUS-MT candidate.

The probe intentionally does not publish model weights. It exports to the ephemeral runner,
records exact files/sizes/hashes/graph signatures and a small ONNX Runtime parity smoke test,
then CI uploads only the JSON report and Python package freeze.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import platform
import shutil
import sys
import traceback
from pathlib import Path
from typing import Any

MODEL_ID = "Helsinki-NLP/opus-mt-en-jap"
REVISION = "a863894cdd2b80f3bc1c5966734aee9ffec207d1"
TASK = "text2text-generation-with-past"
SAMPLES = [
    "I was tired, so I went home.",
    "Please keep off the grass.",
]


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
    return {
        "opsets": [
            {"domain": item.domain, "version": int(item.version)}
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


def run_parity(output: Path) -> list[dict[str, str]]:
    from optimum.onnxruntime import ORTModelForSeq2SeqLM
    from transformers import AutoTokenizer

    tokenizer = AutoTokenizer.from_pretrained(str(output), local_files_only=True)
    model = ORTModelForSeq2SeqLM.from_pretrained(
        str(output),
        provider="CPUExecutionProvider",
        local_files_only=True,
    )
    encoded = tokenizer(SAMPLES, return_tensors="pt", padding=True)
    generated = model.generate(
        **encoded,
        num_beams=4,
        max_new_tokens=64,
        renormalize_logits=True,
    )
    translations = tokenizer.batch_decode(generated, skip_special_tokens=True)
    return [
        {"source": source, "translation": translation}
        for source, translation in zip(SAMPLES, translations)
    ]


def write_report(path: Path, report: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()

    report: dict[str, Any] = {
        "schema_version": 1,
        "model_id": MODEL_ID,
        "revision": REVISION,
        "task": TASK,
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

        if args.output.exists():
            shutil.rmtree(args.output)
        args.output.mkdir(parents=True)

        main_export(
            MODEL_ID,
            output=args.output,
            task=TASK,
            revision=REVISION,
            framework="pt",
            do_validation=True,
        )

        report["files"] = collect_files(args.output)
        report["parity_samples"] = run_parity(args.output)
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
