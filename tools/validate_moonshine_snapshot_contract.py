#!/usr/bin/env python3
"""Validate a local, revision-pinned Moonshine Tiny metadata/tokenizer snapshot.

This tool is intentionally dependency-free and never downloads model weights. A caller stages the
reviewed small files from one immutable Hugging Face revision, then this script checks the ASR/audio
contract and emits SHA-256 evidence suitable for review. Real ONNX/Unity graph compatibility is a
separate gate.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
from typing import Any, Dict, Mapping

MODEL_ID = "moonshine-ai/moonshine-tiny"
EXPECTED_REVISION = "390624ed33d594443aa4aa221f5b9f283b545b5a"
FULL_REVISION_RE = re.compile(r"^[0-9a-f]{40}$")
REQUIRED_FILES = [
    "README.md",
    "config.json",
    "generation_config.json",
    "preprocessor_config.json",
    "tokenizer.json",
]


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


def _sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _extract_tokenizer_vocab_size(tokenizer: Mapping[str, Any]) -> int:
    model = tokenizer.get("model")
    _require(isinstance(model, dict), "tokenizer.json model block is missing")
    vocab = model.get("vocab")
    if isinstance(vocab, dict):
        ids = list(vocab.values())
        _require(all(isinstance(item, int) and not isinstance(item, bool) for item in ids),
                 "tokenizer.json vocabulary ids must be integers")
        _require(len(set(ids)) == len(ids), "tokenizer.json vocabulary ids must be unique")
        return len(vocab)
    if isinstance(vocab, list):
        return len(vocab)
    raise SnapshotContractError("tokenizer.json model.vocab must be an object or list")


def validate_snapshot(snapshot_dir: pathlib.Path, revision: str) -> Dict[str, Any]:
    _require(FULL_REVISION_RE.fullmatch(revision) is not None,
             "Moonshine revision must be a full lowercase 40-character commit SHA")
    _require(revision == EXPECTED_REVISION,
             "Moonshine revision drift: candidate review is pinned to " + EXPECTED_REVISION)
    _require(snapshot_dir.is_dir(), f"snapshot directory does not exist: {snapshot_dir}")

    paths: Dict[str, pathlib.Path] = {}
    for name in REQUIRED_FILES:
        path = snapshot_dir / name
        _require(path.is_file(), f"required Moonshine snapshot artifact is missing: {name}")
        _require(path.stat().st_size > 0, f"required Moonshine snapshot artifact is empty: {name}")
        paths[name] = path

    config = _load_json(paths["config.json"])
    generation = _load_json(paths["generation_config.json"])
    preprocessor = _load_json(paths["preprocessor_config.json"])
    tokenizer = _load_json(paths["tokenizer.json"])
    _require(isinstance(config, dict), "config.json must contain an object")
    _require(isinstance(generation, dict), "generation_config.json must contain an object")
    _require(isinstance(preprocessor, dict), "preprocessor_config.json must contain an object")
    _require(isinstance(tokenizer, dict), "tokenizer.json must contain an object")

    _require(config.get("architectures") == ["MoonshineForConditionalGeneration"],
             "Moonshine architecture drift")
    _require(config.get("model_type") == "moonshine", "Moonshine model_type drift")
    _require(config.get("is_encoder_decoder") is True, "Moonshine encoder-decoder flag drift")
    _require(config.get("use_cache") is True, "Moonshine cache flag drift")
    _require(config.get("vocab_size") == 32768, "Moonshine vocab_size drift")
    _require(config.get("hidden_size") == 288, "Moonshine hidden_size drift")
    _require(config.get("encoder_num_hidden_layers") == 6, "Moonshine encoder layer-count drift")
    _require(config.get("decoder_num_hidden_layers") == 6, "Moonshine decoder layer-count drift")
    _require(config.get("encoder_num_attention_heads") == 8, "Moonshine encoder head-count drift")
    _require(config.get("decoder_num_attention_heads") == 8, "Moonshine decoder head-count drift")
    _require(config.get("max_position_embeddings") == 194, "Moonshine position-limit drift")
    _require(config.get("bos_token_id") == 1, "Moonshine BOS drift")
    _require(config.get("decoder_start_token_id") == 1, "Moonshine decoder-start drift")
    _require(config.get("eos_token_id") == 2, "Moonshine EOS drift")
    _require(config.get("pad_token_id") == 2, "Moonshine PAD drift")

    _require(generation.get("bos_token_id") == 1, "Moonshine generation BOS drift")
    _require(generation.get("decoder_start_token_id") == 1, "Moonshine generation decoder-start drift")
    _require(generation.get("eos_token_id") == 2, "Moonshine generation EOS drift")
    _require(generation.get("pad_token_id") == 2, "Moonshine generation PAD drift")
    _require(generation.get("max_length") == 194, "Moonshine generation max_length drift")

    _require(preprocessor.get("feature_extractor_type") == "Wav2Vec2FeatureExtractor",
             "Moonshine feature extractor drift")
    _require(preprocessor.get("feature_size") == 1, "Moonshine waveform feature-size drift")
    _require(preprocessor.get("sampling_rate") == 16000, "Moonshine sample-rate drift")
    _require(preprocessor.get("do_normalize") is False, "Moonshine input-normalization drift")
    _require(preprocessor.get("return_attention_mask") is True, "Moonshine attention-mask drift")
    _require(preprocessor.get("padding_value") == 0.0, "Moonshine padding-value drift")

    tokenizer_vocab_size = _extract_tokenizer_vocab_size(tokenizer)
    _require(tokenizer_vocab_size == 32768,
             f"Moonshine tokenizer vocabulary drift: expected 32768, got {tokenizer_vocab_size}")

    readme = paths["README.md"].read_text(encoding="utf-8")
    # Hugging Face model-card front matter at the reviewed English snapshot declares MIT.
    # Accept common YAML casing/spacing while requiring the value itself exactly.
    _require(re.search(r"(?mi)^license\s*:\s*mit\s*$", readme) is not None,
             "Moonshine pinned model card must declare license: mit")

    artifacts = []
    for name in REQUIRED_FILES:
        path = paths[name]
        artifacts.append({
            "name": name,
            "size_bytes": path.stat().st_size,
            "sha256": _sha256(path),
        })

    return {
        "schema_version": 1,
        "model_id": MODEL_ID,
        "revision": revision,
        "license": "mit",
        "language": "en",
        "audio_contract": {
            "sampling_rate": 16000,
            "feature_size": 1,
            "normalize": False,
            "return_attention_mask": True,
        },
        "generation_contract": {
            "vocabulary_size": 32768,
            "bos_token_id": 1,
            "decoder_start_token_id": 1,
            "eos_token_id": 2,
            "pad_token_id": 2,
            "max_length": 194,
        },
        "artifacts": artifacts,
        "weights_downloaded": False,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--snapshot-dir", required=True, type=pathlib.Path)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()

    report = validate_snapshot(args.snapshot_dir, args.revision)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({
        "model_id": report["model_id"],
        "revision": report["revision"],
        "artifact_count": len(report["artifacts"]),
        "weights_downloaded": report["weights_downloaded"],
    }, sort_keys=True))


if __name__ == "__main__":
    main()
