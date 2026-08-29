#!/usr/bin/env python3
"""Validate that models.lock.json is cryptographically bound to committed Marian snapshot evidence."""

from __future__ import annotations

import argparse
import json
import pathlib
import re
from typing import Any, Dict, Iterable, List, Mapping

MODEL_ID = "opus-mt-en-jap"
UPSTREAM = "Helsinki-NLP/opus-mt-en-jap"
FULL_REVISION_RE = re.compile(r"^[0-9a-f]{40}$")
EXPECTED_ALLOW_LIST = [
    "README.md",
    "config.json",
    "generation_config.json",
    "tokenizer_config.json",
    "source.spm",
    "target.spm",
    "vocab.json",
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


def _find_candidate(lock: Mapping[str, Any]) -> Mapping[str, Any]:
    candidates = lock.get("candidates")
    _require(isinstance(candidates, list), "models.lock.json candidates must be a list")
    matches = [candidate for candidate in candidates if isinstance(candidate, dict) and candidate.get("id") == MODEL_ID]
    _require(len(matches) == 1, f"models.lock.json must contain exactly one {MODEL_ID} candidate")
    return matches[0]


def _canonical_lock_artifacts(candidate: Mapping[str, Any]) -> List[Dict[str, Any]]:
    raw = candidate.get("metadata_snapshot_artifacts")
    _require(isinstance(raw, list), "Marian lock must contain metadata_snapshot_artifacts")
    output: List[Dict[str, Any]] = []
    for index, item in enumerate(raw):
        _require(isinstance(item, dict), f"metadata_snapshot_artifacts[{index}] must be an object")
        name = item.get("artifact")
        size = item.get("artifact_size_bytes")
        sha = item.get("artifact_sha256")
        _require(isinstance(name, str) and name, f"metadata_snapshot_artifacts[{index}] artifact is invalid")
        _require(isinstance(size, int) and not isinstance(size, bool) and size > 0,
                 f"metadata_snapshot_artifacts[{index}] size is invalid")
        _require(isinstance(sha, str) and re.fullmatch(r"[0-9a-f]{64}", sha) is not None,
                 f"metadata_snapshot_artifacts[{index}] sha256 is invalid")
        output.append({"name": name, "size_bytes": size, "sha256": sha})
    return output


def _canonical_evidence_artifacts(evidence: Mapping[str, Any]) -> List[Dict[str, Any]]:
    raw = evidence.get("artifacts")
    _require(isinstance(raw, list), "evidence artifacts must be a list")
    output: List[Dict[str, Any]] = []
    for index, item in enumerate(raw):
        _require(isinstance(item, dict), f"evidence artifacts[{index}] must be an object")
        name = item.get("name")
        size = item.get("size_bytes")
        sha = item.get("sha256")
        _require(isinstance(name, str) and name, f"evidence artifacts[{index}] name is invalid")
        _require(isinstance(size, int) and not isinstance(size, bool) and size > 0,
                 f"evidence artifacts[{index}] size is invalid")
        _require(isinstance(sha, str) and re.fullmatch(r"[0-9a-f]{64}", sha) is not None,
                 f"evidence artifacts[{index}] sha256 is invalid")
        output.append({"name": name, "size_bytes": size, "sha256": sha})
    return output


def validate_lock_evidence(lock_path: pathlib.Path, repository_root: pathlib.Path) -> Dict[str, Any]:
    lock = _load_json(lock_path)
    _require(isinstance(lock, dict), "models.lock.json must contain an object")
    candidate = _find_candidate(lock)

    revision = candidate.get("revision")
    _require(isinstance(revision, str) and FULL_REVISION_RE.fullmatch(revision) is not None,
             "Marian lock revision must be a full lowercase 40-character commit SHA")
    _require(candidate.get("upstream") == UPSTREAM, "Marian lock upstream drift")
    _require(candidate.get("bundled") is False, "Marian candidate must remain unbundled")

    evidence_relative = candidate.get("evidence_manifest")
    _require(isinstance(evidence_relative, str) and evidence_relative,
             "Marian lock must reference an evidence_manifest")
    evidence_path = repository_root / evidence_relative
    _require(evidence_path.is_file(), f"Marian evidence manifest does not exist: {evidence_relative}")
    evidence = _load_json(evidence_path)
    _require(isinstance(evidence, dict), "Marian evidence manifest must contain an object")

    _require(evidence.get("model_id") == UPSTREAM, "Marian evidence model_id does not match lock upstream")
    _require(evidence.get("revision") == revision, "Marian evidence revision does not match lock revision")

    lock_license = candidate.get("license")
    evidence_license = evidence.get("license")
    _require(isinstance(lock_license, str) and isinstance(evidence_license, str),
             "Marian lock/evidence license fields must be strings")
    _require(lock_license.lower() == evidence_license.lower(), "Marian evidence license does not match lock license")
    _require(evidence_license.lower() == "apache-2.0", "Marian pinned evidence license drift")

    staging = evidence.get("staging")
    _require(isinstance(staging, dict), "Marian evidence staging block is missing")
    _require(staging.get("mode") == "huggingface-small-artifacts-only", "Marian evidence staging mode drift")
    _require(staging.get("weights_downloaded") is False, "Marian evidence must prove weights_downloaded=false")
    _require(staging.get("allow_list") == EXPECTED_ALLOW_LIST, "Marian evidence allow-list drift")

    evidence_artifacts = _canonical_evidence_artifacts(evidence)
    lock_artifacts = _canonical_lock_artifacts(candidate)
    _require([item["name"] for item in evidence_artifacts] == EXPECTED_ALLOW_LIST,
             "Marian evidence artifact order/set does not match reviewed allow-list")
    _require(lock_artifacts == evidence_artifacts,
             "Marian models.lock metadata_snapshot_artifacts do not exactly match committed evidence")

    languages = evidence.get("languages")
    _require(languages == {"source": "en", "target": "jap"}, "Marian evidence language direction drift")
    policy = evidence.get("generation_policy")
    _require(isinstance(policy, dict), "Marian evidence generation_policy is missing")
    _require(policy.get("bad_word_token_ids") == [46275], "Marian evidence PAD ban drift")
    _require(policy.get("forced_eos_token_id") == 0, "Marian evidence forced EOS drift")
    _require(policy.get("upstream_default_beam_width") == 4, "Marian evidence upstream beam-width drift")
    _require(policy.get("phraselayer_parity_beam_width") == 1, "Marian evidence parity beam-width drift")
    _require(policy.get("renormalize_logits") is True, "Marian evidence renormalize_logits drift")

    return {
        "candidate": MODEL_ID,
        "revision": revision,
        "evidence_manifest": evidence_relative,
        "artifact_count": len(evidence_artifacts),
        "license": evidence_license,
        "weights_downloaded": False,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", type=pathlib.Path, default=pathlib.Path("models/models.lock.json"))
    parser.add_argument("--repository-root", type=pathlib.Path, default=pathlib.Path("."))
    args = parser.parse_args()
    report = validate_lock_evidence(args.lock, args.repository_root)
    print(json.dumps(report, sort_keys=True))


if __name__ == "__main__":
    main()
