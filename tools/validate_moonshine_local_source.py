#!/usr/bin/env python3
"""Validate a local Moonshine Tiny source snapshot against committed lock evidence.

This tool never downloads anything and never exports a model. It is the mandatory gate in
front of future ONNX exporters: all five reviewed metadata/tokenizer files and the source
safetensors file must match the repository's exact-revision evidence byte-for-byte.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import pathlib
from typing import Any, Callable, Dict, Mapping

CANDIDATE_ID = "moonshine-tiny"


class LocalSourceError(RuntimeError):
    pass


def _load_local_module(filename: str, module_name: str):
    path = pathlib.Path(__file__).with_name(filename)
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        raise LocalSourceError(f"failed to load helper module: {filename}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _load_json(path: pathlib.Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise LocalSourceError(f"failed to parse {path}: {exc}") from exc


def _sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def fingerprint(path: pathlib.Path) -> Dict[str, Any]:
    if not path.is_file():
        raise LocalSourceError(f"missing local Moonshine source artifact: {path.name}")
    size = path.stat().st_size
    if size <= 0:
        raise LocalSourceError(f"local Moonshine source artifact is empty: {path.name}")
    return {"name": path.name, "size_bytes": size, "sha256": _sha256(path)}


def _candidate(lock: Mapping[str, Any]) -> Mapping[str, Any]:
    candidates = lock.get("candidates")
    if not isinstance(candidates, list):
        raise LocalSourceError("models.lock.json candidates must be a list")
    matches = [item for item in candidates if isinstance(item, dict) and item.get("id") == CANDIDATE_ID]
    if len(matches) != 1:
        raise LocalSourceError(f"models.lock.json must contain exactly one {CANDIDATE_ID} candidate")
    return matches[0]


def validate_local_source(
    source_dir: pathlib.Path,
    lock_path: pathlib.Path,
    repository_root: pathlib.Path,
    *,
    fingerprint_reader: Callable[[pathlib.Path], Dict[str, Any]] = fingerprint,
) -> Dict[str, Any]:
    if not source_dir.is_dir():
        raise LocalSourceError(f"local Moonshine source directory does not exist: {source_dir}")

    validator = _load_local_module("validate_moonshine_lock_evidence.py", "validate_moonshine_lock_evidence")
    try:
        lock_report = validator.validate_lock_evidence(lock_path, repository_root)
    except Exception as exc:
        raise LocalSourceError(f"Moonshine lock/evidence validation failed: {exc}") from exc

    lock = _load_json(lock_path)
    candidate = _candidate(lock)
    evidence = _load_json(repository_root / candidate["evidence_manifest"])

    metadata = []
    for expected in evidence["artifacts"]:
        actual = fingerprint_reader(source_dir / expected["name"])
        if actual != expected:
            raise LocalSourceError(
                "local Moonshine metadata/tokenizer artifact does not match pinned evidence: " + expected["name"]
            )
        metadata.append(actual)

    weight = candidate.get("source_weight_artifact")
    if not isinstance(weight, dict):
        raise LocalSourceError("Moonshine lock is missing source_weight_artifact")
    weight_name = weight.get("artifact")
    if not isinstance(weight_name, str) or not weight_name:
        raise LocalSourceError("Moonshine source weight artifact name is invalid")
    expected_weight = {
        "name": weight_name,
        "size_bytes": weight.get("artifact_size_bytes"),
        "sha256": weight.get("artifact_sha256"),
    }
    actual_weight = fingerprint_reader(source_dir / weight_name)
    if actual_weight != expected_weight:
        raise LocalSourceError(
            "local Moonshine safetensors weight does not match locked upstream identity: " + weight_name
        )

    return {
        "model_id": candidate["upstream"],
        "revision": candidate["revision"],
        "metadata_artifacts": metadata,
        "weight_artifact": actual_weight,
        "lock_evidence": lock_report,
        "ready_for_export": True,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-dir", type=pathlib.Path, required=True)
    parser.add_argument("--lock", type=pathlib.Path, default=pathlib.Path("models/models.lock.json"))
    parser.add_argument("--repository-root", type=pathlib.Path, default=pathlib.Path("."))
    parser.add_argument("--output", type=pathlib.Path)
    args = parser.parse_args()

    report = validate_local_source(args.source_dir, args.lock, args.repository_root)
    rendered = json.dumps(report, indent=2, sort_keys=True) + "\n"
    if args.output is not None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
