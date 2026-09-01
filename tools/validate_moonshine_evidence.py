#!/usr/bin/env python3
"""Validate PhraseLayer's committed Moonshine Tiny small-snapshot evidence.

This validator has no network/model-runtime dependency. It binds the reviewed exact revision and
artifact fingerprints to the Core ASR contract so an evidence file cannot be silently weakened or
replaced while hosted CI remains green.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import re
from typing import Any, Dict

MODEL_ID = "moonshine-ai/moonshine-tiny"
REVISION = "390624ed33d594443aa4aa221f5b9f283b545b5a"
EXPECTED_ARTIFACTS = {
    "README.md": (7959, "e78d23eeed44c568f638f60c0f84437674c12724056714c374ba24d02e278277"),
    "config.json": (897, "47a43777a14e17b1ffd5f533e021d4d18c3c475cbb96de0947ce409e16444ded"),
    "generation_config.json": (189, "a8e1437432c3ba7d0fca84ced5b3a254bf0c42b8e75fc336497cdcb56675e303"),
    "preprocessor_config.json": (215, "99272fe8ccfab114b68b478681ea47ee3a1ce62bb788cb92dd6e4f69fb1f1da2"),
    "tokenizer.json": (1985530, "6579793438bc4fbafffacf699169ff53e3769c5a0a0f5e71cdee8853e8130deb"),
}
EXPECTED_ALLOW_LIST = list(EXPECTED_ARTIFACTS)
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")


class EvidenceError(ValueError):
    pass


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise EvidenceError(message)


def validate_evidence(evidence: Dict[str, Any]) -> None:
    _require(evidence.get("schema_version") == 1, "Moonshine evidence schema drift")
    _require(evidence.get("model_id") == MODEL_ID, "Moonshine evidence model_id drift")
    _require(evidence.get("revision") == REVISION, "Moonshine evidence revision drift")
    _require(evidence.get("language") == "en", "Moonshine evidence language drift")
    _require(evidence.get("license") == "mit", "Moonshine evidence license drift")
    _require(evidence.get("weights_downloaded") is False,
             "Moonshine committed small-snapshot evidence must not represent downloaded weights")

    audio = evidence.get("audio_contract")
    _require(audio == {
        "feature_size": 1,
        "normalize": False,
        "return_attention_mask": True,
        "sampling_rate": 16000,
    }, "Moonshine audio contract evidence drift")

    generation = evidence.get("generation_contract")
    _require(generation == {
        "bos_token_id": 1,
        "decoder_start_token_id": 1,
        "eos_token_id": 2,
        "max_length": 194,
        "pad_token_id": 2,
        "vocabulary_size": 32768,
    }, "Moonshine generation contract evidence drift")

    tokenizer = evidence.get("tokenizer_contract")
    _require(tokenizer == {
        "added_token_entries": 771,
        "base_vocabulary_size": 32000,
        "maximum_token_id": 32767,
        "minimum_token_id": 0,
        "unique_token_id_count": 32768,
    }, "Moonshine tokenizer contract evidence drift")

    staging = evidence.get("staging")
    _require(isinstance(staging, dict), "Moonshine evidence staging block missing")
    _require(staging.get("mode") == "huggingface-small-artifacts-only",
             "Moonshine evidence staging mode drift")
    _require(staging.get("allow_list") == EXPECTED_ALLOW_LIST,
             "Moonshine evidence staging allow-list drift")
    _require(staging.get("weights_downloaded") is False,
             "Moonshine evidence staging must not download weights")

    artifacts = evidence.get("artifacts")
    _require(isinstance(artifacts, list), "Moonshine evidence artifacts must be a list")
    _require(len(artifacts) == len(EXPECTED_ARTIFACTS), "Moonshine evidence artifact-count drift")
    seen = set()
    for index, artifact in enumerate(artifacts):
        _require(isinstance(artifact, dict), f"Moonshine artifact[{index}] must be an object")
        name = artifact.get("name")
        _require(isinstance(name, str) and name in EXPECTED_ARTIFACTS,
                 f"unexpected Moonshine evidence artifact: {name!r}")
        _require(name not in seen, f"duplicate Moonshine evidence artifact: {name}")
        seen.add(name)
        expected_size, expected_sha = EXPECTED_ARTIFACTS[name]
        _require(artifact.get("size_bytes") == expected_size,
                 f"Moonshine evidence size drift for {name}")
        sha = artifact.get("sha256")
        _require(isinstance(sha, str) and SHA256_RE.fullmatch(sha) is not None,
                 f"Moonshine evidence SHA-256 format invalid for {name}")
        _require(sha == expected_sha, f"Moonshine evidence SHA-256 drift for {name}")
    _require(seen == set(EXPECTED_ARTIFACTS), "Moonshine evidence artifact set incomplete")


def load_and_validate(path: pathlib.Path) -> Dict[str, Any]:
    try:
        evidence = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise EvidenceError(f"failed to parse Moonshine evidence: {exc}") from exc
    _require(isinstance(evidence, dict), "Moonshine evidence root must be an object")
    validate_evidence(evidence)
    return evidence


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--evidence",
        type=pathlib.Path,
        default=pathlib.Path("models/evidence/moonshine-tiny.390624ed33d594443aa4aa221f5b9f283b545b5a.snapshot.json"),
    )
    args = parser.parse_args()
    evidence = load_and_validate(args.evidence)
    print(json.dumps({
        "model_id": evidence["model_id"],
        "revision": evidence["revision"],
        "artifact_count": len(evidence["artifacts"]),
        "status": "validated",
    }, sort_keys=True))


if __name__ == "__main__":
    main()
