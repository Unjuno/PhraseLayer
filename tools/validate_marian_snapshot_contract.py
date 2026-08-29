#!/usr/bin/env python3
"""Validate a local revision-pinned Helsinki-NLP/opus-mt-en-jap snapshot.

This tool deliberately does not download or bundle model weights. It validates the small source/tokenizer/config
artifacts that define PhraseLayer's reviewed Marian runtime contract and emits an evidence manifest containing
SHA-256 hashes. The caller must supply the full 40-character upstream commit revision obtained from a trusted
snapshot mechanism; short branch aliases are rejected.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
from typing import Any, Dict, Mapping

FULL_REVISION_RE = re.compile(r"^[0-9a-f]{40}$")
EXPECTED_VOCABULARY_SIZE = 46276
EXPECTED_PAD = 46275
EXPECTED_EOS = 0
EXPECTED_MODEL_DIMENSION = 512
EXPECTED_LAYERS = 6
EXPECTED_HEADS = 8
EXPECTED_MAX_LENGTH = 512
EXPECTED_SOURCE_LANGUAGE = "en"
EXPECTED_TARGET_LANGUAGE = "jap"

REQUIRED_SMALL_ARTIFACTS = (
    "config.json",
    "generation_config.json",
    "tokenizer_config.json",
    "source.spm",
    "target.spm",
    "vocab.json",
)


class SnapshotContractError(ValueError):
    pass


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise SnapshotContractError(message)


def _load_json(path: pathlib.Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise SnapshotContractError(f"failed to parse {path.name}: {exc}") from exc


def _require_field(data: Mapping[str, Any], field: str, expected: Any, source: str) -> None:
    actual = data.get(field)
    _require(
        actual == expected,
        f"{source}: {field} expected {expected!r} but found {actual!r}",
    )


def _sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _validate_config(config: Mapping[str, Any]) -> None:
    architectures = config.get("architectures")
    _require(
        isinstance(architectures, list) and "MarianMTModel" in architectures,
        "config.json: architectures must contain MarianMTModel",
    )
    _require_field(config, "model_type", "marian", "config.json")
    _require_field(config, "is_encoder_decoder", True, "config.json")
    _require_field(config, "vocab_size", EXPECTED_VOCABULARY_SIZE, "config.json")
    _require_field(config, "decoder_vocab_size", EXPECTED_VOCABULARY_SIZE, "config.json")
    _require_field(config, "d_model", EXPECTED_MODEL_DIMENSION, "config.json")
    _require_field(config, "encoder_layers", EXPECTED_LAYERS, "config.json")
    _require_field(config, "decoder_layers", EXPECTED_LAYERS, "config.json")
    _require_field(config, "encoder_attention_heads", EXPECTED_HEADS, "config.json")
    _require_field(config, "decoder_attention_heads", EXPECTED_HEADS, "config.json")
    _require_field(config, "max_position_embeddings", EXPECTED_MAX_LENGTH, "config.json")
    _require_field(config, "max_length", EXPECTED_MAX_LENGTH, "config.json")
    _require_field(config, "bad_words_ids", [[EXPECTED_PAD]], "config.json")
    _require_field(config, "bos_token_id", EXPECTED_EOS, "config.json")
    _require_field(config, "eos_token_id", EXPECTED_EOS, "config.json")
    _require_field(config, "forced_eos_token_id", EXPECTED_EOS, "config.json")
    _require_field(config, "pad_token_id", EXPECTED_PAD, "config.json")
    _require_field(config, "decoder_start_token_id", EXPECTED_PAD, "config.json")
    _require_field(config, "num_beams", 4, "config.json")
    _require_field(config, "use_cache", True, "config.json")


def _validate_generation_config(config: Mapping[str, Any]) -> None:
    _require_field(config, "bad_words_ids", [[EXPECTED_PAD]], "generation_config.json")
    _require_field(config, "bos_token_id", EXPECTED_EOS, "generation_config.json")
    _require_field(config, "decoder_start_token_id", EXPECTED_PAD, "generation_config.json")
    _require_field(config, "eos_token_id", EXPECTED_EOS, "generation_config.json")
    _require_field(config, "forced_eos_token_id", EXPECTED_EOS, "generation_config.json")
    _require_field(config, "max_length", EXPECTED_MAX_LENGTH, "generation_config.json")
    _require_field(config, "num_beams", 4, "generation_config.json")
    _require_field(config, "pad_token_id", EXPECTED_PAD, "generation_config.json")
    _require_field(config, "renormalize_logits", True, "generation_config.json")


def _validate_tokenizer_config(config: Mapping[str, Any]) -> None:
    _require_field(config, "source_lang", EXPECTED_SOURCE_LANGUAGE, "tokenizer_config.json")
    _require_field(config, "target_lang", EXPECTED_TARGET_LANGUAGE, "tokenizer_config.json")


def _validate_vocabulary(vocabulary: Any) -> None:
    _require(isinstance(vocabulary, dict), "vocab.json must contain an object")
    _require(
        len(vocabulary) == EXPECTED_VOCABULARY_SIZE,
        f"vocab.json expected {EXPECTED_VOCABULARY_SIZE} entries but found {len(vocabulary)}",
    )
    ids = list(vocabulary.values())
    _require(all(isinstance(value, int) and not isinstance(value, bool) for value in ids),
             "vocab.json ids must all be integers")
    _require(
        len(set(ids)) == EXPECTED_VOCABULARY_SIZE,
        "vocab.json token ids must be unique",
    )
    _require(
        min(ids) == 0 and max(ids) == EXPECTED_VOCABULARY_SIZE - 1,
        "vocab.json token ids must cover 0..46275",
    )
    _require(vocabulary.get("</s>") == EXPECTED_EOS, "vocab.json </s> must map to EOS id 0")
    _require(vocabulary.get("<pad>") == EXPECTED_PAD, "vocab.json <pad> must map to id 46275")
    _require("<unk>" in vocabulary, "vocab.json must contain <unk>")


def validate_snapshot(snapshot_dir: pathlib.Path, revision: str) -> Dict[str, Any]:
    _require(
        FULL_REVISION_RE.fullmatch(revision) is not None,
        "revision must be a full lowercase 40-character hexadecimal commit SHA",
    )
    _require(snapshot_dir.is_dir(), f"snapshot directory does not exist: {snapshot_dir}")

    paths: Dict[str, pathlib.Path] = {}
    for name in REQUIRED_SMALL_ARTIFACTS:
        path = snapshot_dir / name
        _require(path.is_file(), f"snapshot is missing required artifact: {name}")
        _require(path.stat().st_size > 0, f"snapshot artifact is empty: {name}")
        paths[name] = path

    config = _load_json(paths["config.json"])
    generation_config = _load_json(paths["generation_config.json"])
    tokenizer_config = _load_json(paths["tokenizer_config.json"])
    vocabulary = _load_json(paths["vocab.json"])
    _require(isinstance(config, dict), "config.json must contain an object")
    _require(isinstance(generation_config, dict), "generation_config.json must contain an object")
    _require(isinstance(tokenizer_config, dict), "tokenizer_config.json must contain an object")

    _validate_config(config)
    _validate_generation_config(generation_config)
    _validate_tokenizer_config(tokenizer_config)
    _validate_vocabulary(vocabulary)

    artifacts = []
    for name in REQUIRED_SMALL_ARTIFACTS:
        path = paths[name]
        artifacts.append(
            {
                "name": name,
                "size_bytes": path.stat().st_size,
                "sha256": _sha256(path),
            }
        )

    return {
        "schema_version": 1,
        "model_id": "Helsinki-NLP/opus-mt-en-jap",
        "revision": revision,
        "languages": {
            "source": EXPECTED_SOURCE_LANGUAGE,
            "target": EXPECTED_TARGET_LANGUAGE,
        },
        "generation_policy": {
            "upstream_default_beam_width": 4,
            "phraselayer_parity_beam_width": 1,
            "bad_word_token_ids": [EXPECTED_PAD],
            "forced_eos_token_id": EXPECTED_EOS,
            "renormalize_logits": True,
        },
        "artifacts": artifacts,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--snapshot-dir", type=pathlib.Path, required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()

    manifest = validate_snapshot(args.snapshot_dir, args.revision)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(json.dumps({"model_id": manifest["model_id"], "revision": manifest["revision"]}))


if __name__ == "__main__":
    main()
