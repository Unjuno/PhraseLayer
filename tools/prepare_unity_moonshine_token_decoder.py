#!/usr/bin/env python3
"""Build a verified Moonshine token-decoder TextAsset for Unity Resources.

This tool reads only the pinned tokenizer.json from a local Moonshine Tiny metadata snapshot.
It validates that tokenizer file against committed snapshot evidence before generating the compact
BinTokenizer-compatible decode-only asset. Model weights are neither required nor accessed.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import pathlib
from typing import Any, Dict, Mapping

ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCE_REVISION = "390624ed33d594443aa4aa221f5b9f283b545b5a"
DEFAULT_EVIDENCE = ROOT / f"models/evidence/moonshine-tiny.{SOURCE_REVISION}.snapshot.json"
DEFAULT_DESTINATION = ROOT / "unity/PhraseLayer.Unity/Assets/Resources/LocalAsrAssets"
DEFAULT_OUTPUT = DEFAULT_DESTINATION / "moonshine-tiny.tokens.bytes"
DEFAULT_MANIFEST = DEFAULT_DESTINATION / "moonshine-tiny.tokens.manifest.json"
EXPECTED_TOKENIZER_NAME = "tokenizer.json"


class PrepareError(RuntimeError):
    pass


def _load_module():
    helper = pathlib.Path(__file__).with_name("prepare_moonshine_token_decoder_assets.py")
    spec = importlib.util.spec_from_file_location("prepare_moonshine_token_decoder_assets", helper)
    if spec is None or spec.loader is None:
        raise PrepareError("failed to load Moonshine token decoder helper")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _load_json(path: pathlib.Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise PrepareError(f"failed to parse {path}: {exc}") from exc


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _expected_tokenizer(evidence: Mapping[str, Any]) -> Mapping[str, Any]:
    if evidence.get("model_id") != "moonshine-ai/moonshine-tiny":
        raise PrepareError("Moonshine tokenizer evidence model_id drift")
    if evidence.get("revision") != SOURCE_REVISION:
        raise PrepareError("Moonshine tokenizer evidence revision drift")
    artifacts = evidence.get("artifacts")
    if not isinstance(artifacts, list):
        raise PrepareError("Moonshine tokenizer evidence artifacts must be a list")
    matches = [item for item in artifacts if isinstance(item, dict) and item.get("name") == EXPECTED_TOKENIZER_NAME]
    if len(matches) != 1:
        raise PrepareError("Moonshine tokenizer evidence must contain exactly one tokenizer.json artifact")
    item = matches[0]
    size = item.get("size_bytes")
    sha = item.get("sha256")
    if not isinstance(size, int) or isinstance(size, bool) or size <= 0:
        raise PrepareError("Moonshine tokenizer evidence size is invalid")
    if not isinstance(sha, str) or len(sha) != 64 or any(ch not in "0123456789abcdef" for ch in sha):
        raise PrepareError("Moonshine tokenizer evidence sha256 is invalid")
    return item


def prepare(
    snapshot_dir: pathlib.Path,
    output_path: pathlib.Path = DEFAULT_OUTPUT,
    manifest_path: pathlib.Path = DEFAULT_MANIFEST,
    evidence_path: pathlib.Path = DEFAULT_EVIDENCE,
) -> Dict[str, Any]:
    if not snapshot_dir.is_dir():
        raise PrepareError(f"Moonshine metadata snapshot does not exist: {snapshot_dir}")
    evidence = _load_json(evidence_path)
    if not isinstance(evidence, dict):
        raise PrepareError("Moonshine tokenizer evidence must contain an object")
    expected = _expected_tokenizer(evidence)

    tokenizer_path = snapshot_dir / EXPECTED_TOKENIZER_NAME
    if not tokenizer_path.is_file():
        raise PrepareError("local Moonshine metadata snapshot is missing tokenizer.json")
    tokenizer_bytes = tokenizer_path.read_bytes()
    if len(tokenizer_bytes) != expected["size_bytes"] or _sha256(tokenizer_bytes) != expected["sha256"]:
        raise PrepareError("local Moonshine tokenizer.json does not match committed exact-revision evidence")

    helper = _load_module()
    try:
        generated = helper.prepare(tokenizer_path, output_path, manifest_path)
    except Exception as exc:
        raise PrepareError(f"Moonshine token decoder generation failed: {exc}") from exc

    report = dict(generated)
    report.update({
        "model_id": "moonshine-ai/moonshine-tiny",
        "revision": SOURCE_REVISION,
        "unity_resource_path": "LocalAsrAssets/moonshine-tiny.tokens",
        "weights_required": False,
        "source_tokenizer_verified": True,
    })
    manifest_path.write_text(json.dumps(report, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return report


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--snapshot-dir", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--manifest", type=pathlib.Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--evidence", type=pathlib.Path, default=DEFAULT_EVIDENCE)
    args = parser.parse_args()
    print(json.dumps(prepare(args.snapshot_dir, args.output, args.manifest, args.evidence), sort_keys=True))


if __name__ == "__main__":
    main()
