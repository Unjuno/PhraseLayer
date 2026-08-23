#!/usr/bin/env python3
"""Convert the pinned OPUS-MT SentencePiece/vocab artifacts into PhraseLayer's deterministic managed manifest.

The output is a local runtime artifact and is never uploaded by CI. The translation export report receives only
its identity/size/count metadata so the conversion remains reproducible without redistributing tokenizer files.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
from pathlib import Path
from typing import Any

EXPECTED_MODEL_ID = "Helsinki-NLP/opus-mt-en-jap"
EXPECTED_REVISION = "a863894cdd2b80f3bc1c5966734aee9ffec207d1"
EXPECTED_SOURCE_SHA256 = "375cbed8885a6d369e0493acfc69a066010a86f98f9bac02430cbeb1726934a6"
EXPECTED_TARGET_SHA256 = "7d5ec21daca7dccb7a9df371b699def40ddd9d0c24cef855e44e31a39b96af55"
EXPECTED_VOCAB_SHA256 = "62f7857585e3cd6150bb420830076edede27caac6304778d8d81be41164e469d"
EXPECTED_NORMALIZER_SHA256 = "cab969cc39d743f8402e6fd752a0916e71839bcb27171ca72191336b7f71b4bc"
EXPECTED_SOURCE_TOTAL_PIECES = 15882
EXPECTED_SOURCE_NORMAL_PIECES = 15879
EXPECTED_TARGET_TOTAL_PIECES = 32000
EXPECTED_MARIAN_VOCAB = 46276
MAGIC = "PHRASELAYER_SENTENCEPIECE_UNIGRAM_V1"


class ManagedManifestError(RuntimeError):
    pass


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def b64(value: str) -> str:
    return base64.b64encode(value.encode("utf-8")).decode("ascii")


def load_report(path: Path) -> dict[str, Any]:
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("model_id") != EXPECTED_MODEL_ID or report.get("revision") != EXPECTED_REVISION:
        raise ManagedManifestError("translation probe identity drift")
    if report.get("status") != "pass":
        raise ManagedManifestError("managed tokenizer conversion requires status=pass")
    if not report.get("parity", {}).get("exact") or not report.get("tokenizer_parity", {}).get("exact"):
        raise ManagedManifestError("managed tokenizer conversion requires exact model/tokenizer parity")
    sp = report.get("sentencepiece_contract")
    if not isinstance(sp, dict) or sp.get("status") != "measured-export-artifacts":
        raise ManagedManifestError("measured SentencePiece contract is missing from probe report")
    return report


def load_model(path: Path):
    import sentencepiece as spm
    from sentencepiece import sentencepiece_model_pb2 as pb

    processor = spm.SentencePieceProcessor(model_file=str(path))
    model = pb.ModelProto()
    model.ParseFromString(processor.serialized_model_proto())
    return processor, model, pb


def require_file_identity(path: Path, expected_sha: str) -> None:
    if not path.is_file():
        raise ManagedManifestError(f"required tokenizer artifact is missing: {path}")
    digest = sha256_file(path)
    if digest != expected_sha:
        raise ManagedManifestError(f"tokenizer artifact SHA-256 drift for {path.name}: {digest}")


def append_header(lines: list[str], key: str, value: str) -> None:
    if not key or "\t" in key or "\n" in key:
        raise ManagedManifestError("invalid managed manifest header key")
    if "\t" in value or "\n" in value or "\r" in value:
        raise ManagedManifestError(f"invalid managed manifest header value for {key}")
    lines.append(key + "\t" + value)


def prepare(export_root: Path, report_path: Path, output: Path) -> dict[str, Any]:
    report = load_report(report_path)
    source_path = export_root / "source.spm"
    target_path = export_root / "target.spm"
    vocab_path = export_root / "vocab.json"
    require_file_identity(source_path, EXPECTED_SOURCE_SHA256)
    require_file_identity(target_path, EXPECTED_TARGET_SHA256)
    require_file_identity(vocab_path, EXPECTED_VOCAB_SHA256)

    source_processor, source_model, pb = load_model(source_path)
    target_processor, target_model, _ = load_model(target_path)

    if pb.TrainerSpec.ModelType.Name(source_model.trainer_spec.model_type) != "UNIGRAM":
        raise ManagedManifestError("source SentencePiece model is not UNIGRAM")
    if pb.TrainerSpec.ModelType.Name(target_model.trainer_spec.model_type) != "UNIGRAM":
        raise ManagedManifestError("target SentencePiece model is not UNIGRAM")
    if source_processor.vocab_size() != EXPECTED_SOURCE_TOTAL_PIECES:
        raise ManagedManifestError("source SentencePiece piece-count drift")
    if target_processor.vocab_size() != EXPECTED_TARGET_TOTAL_PIECES:
        raise ManagedManifestError("target SentencePiece piece-count drift")

    source_normalizer = source_model.normalizer_spec
    target_normalizer = target_model.normalizer_spec
    for label, normalizer in (("source", source_normalizer), ("target", target_normalizer)):
        digest = hashlib.sha256(bytes(normalizer.precompiled_charsmap)).hexdigest()
        if normalizer.name != "nmt_nfkc" or digest != EXPECTED_NORMALIZER_SHA256:
            raise ManagedManifestError(f"{label} SentencePiece normalizer identity drift")
        if not normalizer.add_dummy_prefix or not normalizer.remove_extra_whitespaces or not normalizer.escape_whitespaces:
            raise ManagedManifestError(f"{label} SentencePiece whitespace contract drift")

    vocab = json.loads(vocab_path.read_text(encoding="utf-8"))
    if not isinstance(vocab, dict) or len(vocab) != EXPECTED_MARIAN_VOCAB:
        raise ManagedManifestError("Marian vocab.json size drift")
    ids = sorted(int(value) for value in vocab.values())
    if ids != list(range(EXPECTED_MARIAN_VOCAB)):
        raise ManagedManifestError("Marian vocabulary ids must be contiguous [0,vocab_size)")
    if vocab.get("</s>") != 0 or vocab.get("<unk>") != 1 or vocab.get("<pad>") != 46275:
        raise ManagedManifestError("Marian special token ids drift")

    normal_type = pb.ModelProto.SentencePiece.NORMAL
    source_pieces = [piece for piece in source_model.pieces if piece.type == normal_type]
    if len(source_pieces) != EXPECTED_SOURCE_NORMAL_PIECES:
        raise ManagedManifestError(
            f"source NORMAL piece count drift: expected {EXPECTED_SOURCE_NORMAL_PIECES}, got {len(source_pieces)}"
        )

    lines: list[str] = [MAGIC]
    append_header(lines, "model_id_b64", b64(EXPECTED_MODEL_ID))
    append_header(lines, "revision", EXPECTED_REVISION)
    append_header(lines, "model_type", "UNIGRAM")
    append_header(lines, "normalizer_name", "nmt_nfkc")
    append_header(lines, "normalizer_charsmap_sha256", EXPECTED_NORMALIZER_SHA256)
    append_header(lines, "source_total_piece_count", str(EXPECTED_SOURCE_TOTAL_PIECES))
    append_header(lines, "source_normal_piece_count", str(EXPECTED_SOURCE_NORMAL_PIECES))
    append_header(lines, "target_total_piece_count", str(EXPECTED_TARGET_TOTAL_PIECES))
    append_header(lines, "marian_vocab_count", str(EXPECTED_MARIAN_VOCAB))
    append_header(lines, "marian_unknown_token_id", "1")
    append_header(lines, "marian_eos_token_id", "0")
    append_header(lines, "byte_fallback", "true" if source_model.trainer_spec.byte_fallback else "false")
    append_header(lines, "add_dummy_prefix", "true")
    append_header(lines, "remove_extra_whitespaces", "true")
    append_header(lines, "escape_whitespaces", "true")
    lines.append("END_HEADER")

    seen_source_ids: set[int] = set()
    for piece in source_pieces:
        model_token_id = vocab.get(piece.piece)
        if not isinstance(model_token_id, int):
            raise ManagedManifestError(f"source SentencePiece token is absent from Marian vocab.json: {piece.piece!r}")
        if model_token_id in seen_source_ids:
            raise ManagedManifestError(f"two source pieces map to Marian token id {model_token_id}")
        seen_source_ids.add(model_token_id)
        score = format(float(piece.score), ".9g")
        lines.append("S\t" + str(model_token_id) + "\t" + score + "\t" + b64(piece.piece))

    inverse_vocab: list[str | None] = [None] * EXPECTED_MARIAN_VOCAB
    for piece, token_id in vocab.items():
        if inverse_vocab[token_id] is not None:
            raise ManagedManifestError(f"duplicate Marian token id: {token_id}")
        inverse_vocab[token_id] = piece
    for token_id, piece in enumerate(inverse_vocab):
        if piece is None:
            raise ManagedManifestError(f"Marian token id {token_id} has no vocabulary piece")
        lines.append("V\t" + str(token_id) + "\t" + b64(piece))
    lines.append("END")

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    result = {
        "format": MAGIC,
        "status": "ephemeral-local-runtime-artifact",
        "size_bytes": output.stat().st_size,
        "sha256": sha256_file(output),
        "source_normal_piece_count": len(source_pieces),
        "marian_vocab_count": EXPECTED_MARIAN_VOCAB,
        "uploaded": False,
    }
    report["managed_tokenizer_manifest"] = result
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--export-root", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = prepare(args.export_root, args.report, args.output)
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
