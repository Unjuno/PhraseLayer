#!/usr/bin/env python3
"""Inspect the exact Marian source SentencePiece model using Google's reference implementation."""

from __future__ import annotations

import argparse
import json
import pathlib
from typing import Any

import sentencepiece as spm
from sentencepiece import sentencepiece_model_pb2

SUSPECT_PIECES = ["0%", "0", "%", "$9", "$", "9", "99", "東京", "東", "京"]
SUSPECT_TEXTS = ["50% OFF — $9.99!", "Tokyo 東京 Station"]


def inspect(model_path: pathlib.Path) -> dict[str, Any]:
    raw = model_path.read_bytes()
    proto = sentencepiece_model_pb2.ModelProto()
    proto.ParseFromString(raw)
    processor = spm.SentencePieceProcessor(model_proto=raw)

    trainer = proto.trainer_spec
    pieces_by_surface = {piece.piece: (index, piece) for index, piece in enumerate(proto.pieces)}
    suspect: list[dict[str, Any]] = []
    for surface in SUSPECT_PIECES:
        found = pieces_by_surface.get(surface)
        if found is None:
            suspect.append({"piece": surface, "present": False})
            continue
        index, piece = found
        suspect.append(
            {
                "piece": surface,
                "present": True,
                "internal_id": index,
                "score": piece.score,
                "type": int(piece.type),
                "processor_piece_to_id": processor.piece_to_id(surface),
                "processor_is_unknown": processor.is_unknown(index),
                "processor_is_control": processor.is_control(index),
                "processor_is_unused": processor.is_unused(index),
                "processor_is_byte": processor.is_byte(index),
            }
        )

    encodings = []
    for text in SUSPECT_TEXTS:
        ids = processor.encode(text, out_type=int)
        encodings.append(
            {
                "text": text,
                "pieces": processor.encode(text, out_type=str),
                "internal_ids": ids,
                "scores": [processor.get_score(token_id) for token_id in ids],
            }
        )

    return {
        "schema_version": 1,
        "piece_count": len(proto.pieces),
        "trainer": {
            "model_type": int(trainer.model_type),
            "vocab_size": trainer.vocab_size,
            "split_by_unicode_script": trainer.split_by_unicode_script,
            "split_by_number": trainer.split_by_number,
            "split_by_whitespace": trainer.split_by_whitespace,
            "split_digits": trainer.split_digits,
            "treat_whitespace_as_suffix": trainer.treat_whitespace_as_suffix,
            "byte_fallback": trainer.byte_fallback,
            "unk_id": trainer.unk_id,
            "bos_id": trainer.bos_id,
            "eos_id": trainer.eos_id,
            "pad_id": trainer.pad_id,
        },
        "normalizer": {
            "name": proto.normalizer_spec.name,
            "add_dummy_prefix": proto.normalizer_spec.add_dummy_prefix,
            "remove_extra_whitespaces": proto.normalizer_spec.remove_extra_whitespaces,
            "escape_whitespaces": proto.normalizer_spec.escape_whitespaces,
            "precompiled_charsmap_bytes": len(proto.normalizer_spec.precompiled_charsmap),
        },
        "suspect_pieces": suspect,
        "reference_encodings": encodings,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    report = inspect(args.model)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, sort_keys=True))


if __name__ == "__main__":
    main()
