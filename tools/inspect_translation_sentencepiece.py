#!/usr/bin/env python3
"""Append measured SentencePiece model metadata to a successful translation export probe report.

The script reads only the already-exported tokenizer artifacts. It does not download model weights and it does
not claim runtime compatibility. The goal is to make the eventual managed Unity tokenizer implementation depend
on a measured SentencePiece contract instead of assumptions about model type or normalization.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any

EXPECTED_MODEL_ID = "Helsinki-NLP/opus-mt-en-jap"
EXPECTED_REVISION = "a863894cdd2b80f3bc1c5966734aee9ffec207d1"
MODEL_FILES = ("source.spm", "target.spm")


class SentencePieceInspectionError(RuntimeError):
    pass


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def enum_name(enum_type: Any, value: int) -> str:
    try:
        return str(enum_type.Name(value))
    except (AttributeError, ValueError):
        return str(value)


def inspect_model(path: Path) -> dict[str, Any]:
    import sentencepiece as spm
    from sentencepiece import sentencepiece_model_pb2 as pb

    if not path.is_file():
        raise SentencePieceInspectionError(f"SentencePiece model is missing: {path}")

    processor = spm.SentencePieceProcessor(model_file=str(path))
    model = pb.ModelProto()
    model.ParseFromString(processor.serialized_model_proto())

    trainer = model.trainer_spec
    normalizer = model.normalizer_spec
    piece_type_counts = Counter(
        enum_name(pb.ModelProto.SentencePiece.Type, int(piece.type))
        for piece in model.pieces
    )

    return {
        "file": path.name,
        "size_bytes": path.stat().st_size,
        "sha256": sha256_file(path),
        "model_type": enum_name(pb.TrainerSpec.ModelType, int(trainer.model_type)),
        "vocab_size": int(processor.vocab_size()),
        "processor_ids": {
            "unk": int(processor.unk_id()),
            "bos": int(processor.bos_id()),
            "eos": int(processor.eos_id()),
            "pad": int(processor.pad_id()),
        },
        "trainer": {
            "byte_fallback": bool(trainer.byte_fallback),
            "split_by_unicode_script": bool(trainer.split_by_unicode_script),
            "split_by_number": bool(trainer.split_by_number),
            "split_by_whitespace": bool(trainer.split_by_whitespace),
            "split_digits": bool(trainer.split_digits),
            "treat_whitespace_as_suffix": bool(trainer.treat_whitespace_as_suffix),
            "allow_whitespace_only_pieces": bool(trainer.allow_whitespace_only_pieces),
        },
        "normalizer": {
            "name": str(normalizer.name),
            "add_dummy_prefix": bool(normalizer.add_dummy_prefix),
            "remove_extra_whitespaces": bool(normalizer.remove_extra_whitespaces),
            "escape_whitespaces": bool(normalizer.escape_whitespaces),
            "precompiled_charsmap_size_bytes": len(normalizer.precompiled_charsmap),
            "precompiled_charsmap_sha256": sha256_bytes(bytes(normalizer.precompiled_charsmap)),
        },
        "piece_type_counts": dict(sorted(piece_type_counts.items())),
    }


def load_report(path: Path) -> dict[str, Any]:
    if not path.is_file():
        raise SentencePieceInspectionError(f"translation export report is missing: {path}")
    report = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(report, dict):
        raise SentencePieceInspectionError("translation export report must be a JSON object")
    if report.get("model_id") != EXPECTED_MODEL_ID:
        raise SentencePieceInspectionError("translation export report model_id drift")
    if report.get("revision") != EXPECTED_REVISION:
        raise SentencePieceInspectionError("translation export report revision drift")
    if report.get("status") != "pass":
        raise SentencePieceInspectionError("SentencePiece inspection requires a successful export/parity report")
    parity = report.get("parity")
    tokenizer_parity = report.get("tokenizer_parity")
    if not isinstance(parity, dict) or parity.get("exact") is not True:
        raise SentencePieceInspectionError("generation parity must be exact before tokenizer contract promotion")
    if not isinstance(tokenizer_parity, dict) or tokenizer_parity.get("exact") is not True:
        raise SentencePieceInspectionError("tokenizer parity must be exact before tokenizer contract promotion")
    return report


def verify_report_identity(report: dict[str, Any], measured: dict[str, Any]) -> None:
    files = report.get("files")
    if not isinstance(files, list):
        raise SentencePieceInspectionError("translation export report contains no file inventory")
    inventory = {
        item.get("path"): item
        for item in files
        if isinstance(item, dict) and isinstance(item.get("path"), str)
    }
    item = inventory.get(measured["file"])
    if not isinstance(item, dict):
        raise SentencePieceInspectionError(f"probe inventory is missing {measured['file']}")
    if item.get("size_bytes") != measured["size_bytes"] or item.get("sha256") != measured["sha256"]:
        raise SentencePieceInspectionError(
            f"SentencePiece identity drift for {measured['file']}: export inventory does not match bytes on disk"
        )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--export-root", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()

    report = load_report(args.report)
    if not args.export_root.is_dir():
        raise SentencePieceInspectionError(f"translation export root is missing: {args.export_root}")

    measured = [inspect_model(args.export_root / name) for name in MODEL_FILES]
    for item in measured:
        verify_report_identity(report, item)

    report["sentencepiece_contract"] = {
        "schema_version": 1,
        "status": "measured-export-artifacts",
        "runtime_compatibility": "unverified-managed-unity-tokenizer-required",
        "models": measured,
    }
    args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    for item in measured:
        print(
            item["file"],
            "model_type=" + item["model_type"],
            "vocab=" + str(item["vocab_size"]),
            "normalizer=" + item["normalizer"]["name"],
            "ids=" + json.dumps(item["processor_ids"], sort_keys=True),
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
