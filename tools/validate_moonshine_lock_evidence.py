#!/usr/bin/env python3
"""Validate that the Moonshine Tiny lock entry matches committed exact-revision evidence."""

from __future__ import annotations

import argparse
import json
import pathlib
import re
from typing import Any, Dict, List, Mapping

MODEL_ID = "moonshine-tiny"
UPSTREAM = "moonshine-ai/moonshine-tiny"
REVISION = "390624ed33d594443aa4aa221f5b9f283b545b5a"
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
EXPECTED_ALLOW_LIST = [
    "README.md",
    "config.json",
    "generation_config.json",
    "preprocessor_config.json",
    "tokenizer.json",
]


class LockEvidenceError(ValueError):
    pass


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise LockEvidenceError(message)


def _load_json(path: pathlib.Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise LockEvidenceError(f"failed to parse {path}: {exc}") from exc


def _candidate(lock: Mapping[str, Any]) -> Mapping[str, Any]:
    candidates = lock.get("candidates")
    _require(isinstance(candidates, list), "models.lock.json candidates must be a list")
    matches = [item for item in candidates if isinstance(item, dict) and item.get("id") == MODEL_ID]
    _require(len(matches) == 1, f"models.lock.json must contain exactly one {MODEL_ID} candidate")
    return matches[0]


def _lock_artifacts(candidate: Mapping[str, Any]) -> List[Dict[str, Any]]:
    raw = candidate.get("metadata_snapshot_artifacts")
    _require(isinstance(raw, list), "Moonshine lock metadata_snapshot_artifacts must be a list")
    result: List[Dict[str, Any]] = []
    for index, item in enumerate(raw):
        _require(isinstance(item, dict), f"metadata_snapshot_artifacts[{index}] must be an object")
        name = item.get("artifact")
        size = item.get("artifact_size_bytes")
        sha = item.get("artifact_sha256")
        _require(isinstance(name, str) and name, f"metadata_snapshot_artifacts[{index}] name is invalid")
        _require(isinstance(size, int) and not isinstance(size, bool) and size > 0,
                 f"metadata_snapshot_artifacts[{index}] size is invalid")
        _require(isinstance(sha, str) and SHA256_RE.fullmatch(sha) is not None,
                 f"metadata_snapshot_artifacts[{index}] sha256 is invalid")
        result.append({"name": name, "size_bytes": size, "sha256": sha})
    return result


def _evidence_artifacts(evidence: Mapping[str, Any]) -> List[Dict[str, Any]]:
    raw = evidence.get("artifacts")
    _require(isinstance(raw, list), "Moonshine evidence artifacts must be a list")
    result: List[Dict[str, Any]] = []
    for index, item in enumerate(raw):
        _require(isinstance(item, dict), f"evidence artifacts[{index}] must be an object")
        name = item.get("name")
        size = item.get("size_bytes")
        sha = item.get("sha256")
        _require(isinstance(name, str) and name, f"evidence artifacts[{index}] name is invalid")
        _require(isinstance(size, int) and not isinstance(size, bool) and size > 0,
                 f"evidence artifacts[{index}] size is invalid")
        _require(isinstance(sha, str) and SHA256_RE.fullmatch(sha) is not None,
                 f"evidence artifacts[{index}] sha256 is invalid")
        result.append({"name": name, "size_bytes": size, "sha256": sha})
    return result


def validate_lock_evidence(lock_path: pathlib.Path, repository_root: pathlib.Path) -> Dict[str, Any]:
    lock = _load_json(lock_path)
    _require(isinstance(lock, dict), "models.lock.json must contain an object")
    candidate = _candidate(lock)

    _require(candidate.get("upstream") == UPSTREAM, "Moonshine lock upstream drift")
    _require(candidate.get("revision") == REVISION, "Moonshine lock revision drift")
    _require(candidate.get("bundled") is False, "Moonshine candidate must remain unbundled")
    _require(str(candidate.get("license", "")).lower() == "mit", "Moonshine lock license drift")
    _require(candidate.get("sample_rate") == 16000, "Moonshine lock sample-rate drift")
    _require(candidate.get("vocab_size") == 32768, "Moonshine lock vocabulary drift")
    _require(candidate.get("base_tokenizer_vocab_size") == 32000, "Moonshine base vocabulary drift")
    _require(candidate.get("added_token_entries") == 771, "Moonshine added-token count drift")
    _require(candidate.get("decoder_start_token_id") == 1, "Moonshine decoder-start drift")
    _require(candidate.get("eos_token_id") == 2, "Moonshine EOS drift")
    _require(candidate.get("pad_token_id") == 2, "Moonshine PAD drift")
    _require(candidate.get("max_generation_length") == 194, "Moonshine generation limit drift")

    evidence_relative = candidate.get("evidence_manifest")
    _require(isinstance(evidence_relative, str) and evidence_relative,
             "Moonshine lock must reference an evidence_manifest")
    evidence_path = repository_root / evidence_relative
    _require(evidence_path.is_file(), f"Moonshine evidence manifest does not exist: {evidence_relative}")
    evidence = _load_json(evidence_path)
    _require(isinstance(evidence, dict), "Moonshine evidence manifest must contain an object")

    _require(evidence.get("model_id") == UPSTREAM, "Moonshine evidence model_id drift")
    _require(evidence.get("revision") == REVISION, "Moonshine evidence revision drift")
    _require(str(evidence.get("license", "")).lower() == "mit", "Moonshine evidence license drift")
    _require(evidence.get("language") == "en", "Moonshine evidence language drift")
    _require(evidence.get("weights_downloaded") is False, "Moonshine evidence must prove weights_downloaded=false")

    staging = evidence.get("staging")
    _require(isinstance(staging, dict), "Moonshine evidence staging block is missing")
    _require(staging.get("mode") == "huggingface-small-artifacts-only", "Moonshine staging mode drift")
    _require(staging.get("allow_list") == EXPECTED_ALLOW_LIST, "Moonshine staging allow-list drift")
    _require(staging.get("weights_downloaded") is False, "Moonshine staging must prove weights_downloaded=false")

    audio = evidence.get("audio_contract")
    _require(audio == {
        "feature_size": 1,
        "normalize": False,
        "return_attention_mask": True,
        "sampling_rate": 16000,
    }, "Moonshine audio contract drift")
    generation = evidence.get("generation_contract")
    _require(generation == {
        "bos_token_id": 1,
        "decoder_start_token_id": 1,
        "eos_token_id": 2,
        "max_length": 194,
        "pad_token_id": 2,
        "vocabulary_size": 32768,
    }, "Moonshine generation contract drift")
    tokenizer = evidence.get("tokenizer_contract")
    _require(tokenizer == {
        "added_token_entries": 771,
        "base_vocabulary_size": 32000,
        "maximum_token_id": 32767,
        "minimum_token_id": 0,
        "unique_token_id_count": 32768,
    }, "Moonshine tokenizer contract drift")

    lock_artifacts = _lock_artifacts(candidate)
    evidence_artifacts = _evidence_artifacts(evidence)
    _require([item["name"] for item in evidence_artifacts] == EXPECTED_ALLOW_LIST,
             "Moonshine evidence artifact order/set drift")
    _require(lock_artifacts == evidence_artifacts,
             "Moonshine models.lock metadata_snapshot_artifacts do not exactly match committed evidence")

    return {
        "candidate": MODEL_ID,
        "upstream": UPSTREAM,
        "revision": REVISION,
        "evidence_manifest": evidence_relative,
        "artifact_count": len(evidence_artifacts),
        "tokenizer_id_count": tokenizer["unique_token_id_count"],
        "weights_downloaded": False,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", type=pathlib.Path, default=pathlib.Path("models/models.lock.json"))
    parser.add_argument("--repository-root", type=pathlib.Path, default=pathlib.Path("."))
    args = parser.parse_args()
    print(json.dumps(validate_lock_evidence(args.lock, args.repository_root), sort_keys=True))


if __name__ == "__main__":
    main()
