#!/usr/bin/env python3
"""Validate and fingerprint a local Helsinki-NLP/opus-mt-en-jap snapshot and optional ONNX export.

This tool never downloads model data. Supply a revision-pinned local snapshot produced by a reviewed
Hugging Face workflow. If an ONNX directory is supplied it must be the explicit three-graph export from:

    optimum-cli export onnx \
      --model <REVISION_PINNED_LOCAL_SNAPSHOT> \
      --task text2text-generation-with-past \
      --no-post-process \
      <OUTPUT_DIR>

The output manifest is an evidence artifact for later Unity import/Quest validation; producing it does not
claim that Unity Inference can execute the graphs.
"""

import argparse
import hashlib
import json
import re
from pathlib import Path

MODEL_ID = "Helsinki-NLP/opus-mt-en-jap"
HEX40 = re.compile(r"^[0-9a-f]{40}$")

EXPECTED_CONFIG = {
    "model_type": "marian",
    "vocab_size": 46276,
    "decoder_vocab_size": 46276,
    "d_model": 512,
    "encoder_layers": 6,
    "decoder_layers": 6,
    "max_position_embeddings": 512,
    "bos_token_id": 0,
    "eos_token_id": 0,
    "pad_token_id": 46275,
    "decoder_start_token_id": 46275,
}
EXPECTED_GENERATION = {
    "bos_token_id": 0,
    "eos_token_id": 0,
    "forced_eos_token_id": 0,
    "pad_token_id": 46275,
    "decoder_start_token_id": 46275,
    "max_length": 512,
    "num_beams": 4,
    "renormalize_logits": True,
}
EXPECTED_TOKENIZER = {
    "source_lang": "en",
    "target_lang": "jap",
}
SNAPSHOT_FILES = (
    "config.json",
    "generation_config.json",
    "tokenizer_config.json",
    "vocab.json",
    "source.spm",
    "target.spm",
    "pytorch_model.bin",
)
ONNX_FILES = (
    "encoder_model.onnx",
    "decoder_model.onnx",
    "decoder_with_past_model.onnx",
)


def fail(message: str) -> None:
    raise ValueError(message)


def read_json(path: Path):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        fail(f"missing file: {path}")
    except json.JSONDecodeError as error:
        fail(f"invalid JSON in {path}: {error}")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while True:
            chunk = stream.read(1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def fingerprint(path: Path) -> dict:
    if not path.is_file():
        fail(f"missing file: {path}")
    size = path.stat().st_size
    if size <= 0:
        fail(f"artifact must not be empty: {path}")
    return {
        "path": path.name,
        "size_bytes": size,
        "sha256": sha256(path),
    }


def require_equal(actual, expected, label: str) -> None:
    if actual != expected:
        fail(f"{label} expected {expected!r} but found {actual!r}")


def validate_snapshot(snapshot_dir: Path) -> list[dict]:
    if not snapshot_dir.is_dir():
        fail(f"snapshot directory does not exist: {snapshot_dir}")

    config = read_json(snapshot_dir / "config.json")
    generation = read_json(snapshot_dir / "generation_config.json")
    tokenizer = read_json(snapshot_dir / "tokenizer_config.json")
    vocab = read_json(snapshot_dir / "vocab.json")

    architectures = config.get("architectures")
    require_equal(architectures, ["MarianMTModel"], "config.architectures")
    for key, expected in EXPECTED_CONFIG.items():
        require_equal(config.get(key), expected, f"config.{key}")
    for key, expected in EXPECTED_GENERATION.items():
        require_equal(generation.get(key), expected, f"generation_config.{key}")
    for key, expected in EXPECTED_TOKENIZER.items():
        require_equal(tokenizer.get(key), expected, f"tokenizer_config.{key}")

    if not isinstance(vocab, dict):
        fail("vocab.json must contain a JSON object")
    expected_vocab_size = EXPECTED_CONFIG["vocab_size"]
    require_equal(len(vocab), expected_vocab_size, "vocab token count")
    vocab_values = list(vocab.values())
    if not all(type(value) is int for value in vocab_values):
        fail("vocab.json token ids must be integers")
    vocab_ids = set(vocab_values)
    require_equal(len(vocab_ids), expected_vocab_size, "unique vocab token id count")
    require_equal(min(vocab_ids), 0, "minimum vocab token id")
    require_equal(max(vocab_ids), expected_vocab_size - 1, "maximum vocab token id")

    return [fingerprint(snapshot_dir / name) for name in SNAPSHOT_FILES]


def validate_onnx(onnx_dir: Path) -> list[dict]:
    if not onnx_dir.is_dir():
        fail(f"ONNX directory does not exist: {onnx_dir}")
    return [fingerprint(onnx_dir / name) for name in ONNX_FILES]


def build_manifest(snapshot_dir: Path, revision: str, onnx_dir: Path | None) -> dict:
    if not HEX40.fullmatch(revision):
        fail("revision must be a full lowercase 40-character Git SHA")

    manifest = {
        "schema_version": 1,
        "model_id": MODEL_ID,
        "revision": revision,
        "license_observed_upstream": "Apache-2.0",
        "redistribution_review": "pending",
        "architecture": "MarianMTModel",
        "source_language": "en",
        "target_language": "jap",
        "preprocessing": "normalization + SentencePiece",
        "runtime_target": "com.unity.ai.inference@2.2.1",
        "runtime_compatibility": "unverified-real-unity-import-and-quest-execution-required",
        "snapshot_artifacts": validate_snapshot(snapshot_dir),
        "onnx_export": {
            "task": "text2text-generation-with-past",
            "no_post_process": True,
            "artifacts": validate_onnx(onnx_dir) if onnx_dir is not None else [],
            "status": "fingerprinted" if onnx_dir is not None else "not-supplied",
        },
    }
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--snapshot-dir", required=True, type=Path)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--onnx-dir", type=Path)
    parser.add_argument("--output-manifest", required=True, type=Path)
    args = parser.parse_args()

    manifest = build_manifest(args.snapshot_dir, args.revision, args.onnx_dir)
    args.output_manifest.parent.mkdir(parents=True, exist_ok=True)
    args.output_manifest.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(
        "PASS: validated Marian en->jap snapshot; "
        f"onnx={manifest['onnx_export']['status']}; manifest={args.output_manifest}"
    )


if __name__ == "__main__":
    main()
