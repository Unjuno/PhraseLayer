#!/usr/bin/env python3
"""Verify and stage the pinned Moonshine v1 reference ONNX bundle for Unity.

No network access is performed. The caller supplies a local snapshot root containing the
four published graphs. Every graph must match the committed evidence manifest exactly
before anything is copied into the Unity project. Model weights/graphs remain git-ignored.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import shutil
from typing import Any, Dict, List, Mapping

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEFAULT_EVIDENCE = ROOT / "models/evidence/moonshine-v1-tiny.35b4aae79f7d598a4d36d5252ec26ad642faab60.onnx.json"
DEFAULT_DESTINATION = ROOT / "unity/PhraseLayer.Unity/Assets/LocalAsrAssets/MoonshineV1Tiny"
DEFAULT_MANIFEST = DEFAULT_DESTINATION / "moonshine-v1-tiny.staging.json"
EXPECTED_MODEL_ID = "moonshine-ai/moonshine"
EXPECTED_REVISION = "35b4aae79f7d598a4d36d5252ec26ad642faab60"
EXPECTED_ARTIFACTS = (
    "onnx/tiny/preprocess.onnx",
    "onnx/tiny/encode.onnx",
    "onnx/tiny/uncached_decode.onnx",
    "onnx/tiny/cached_decode.onnx",
)


class PrepareError(RuntimeError):
    pass


def _load_json(path: pathlib.Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise PrepareError(f"failed to parse {path}: {exc}") from exc


def _sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _fingerprint(path: pathlib.Path) -> Dict[str, Any]:
    if not path.is_file():
        raise PrepareError(f"missing Moonshine reference graph: {path}")
    size = path.stat().st_size
    if size <= 0:
        raise PrepareError(f"Moonshine reference graph is empty: {path}")
    return {"size_bytes": size, "sha256": _sha256(path)}


def _validate_evidence(evidence: Mapping[str, Any]) -> List[Mapping[str, Any]]:
    if evidence.get("schema_version") != 1:
        raise PrepareError("unsupported Moonshine v1 evidence schema")
    if evidence.get("model_id") != EXPECTED_MODEL_ID:
        raise PrepareError("Moonshine v1 evidence model_id drift")
    if evidence.get("revision") != EXPECTED_REVISION:
        raise PrepareError("Moonshine v1 evidence revision drift")
    if evidence.get("bundle_kind") != "moonshine-v1-four-graph":
        raise PrepareError("Moonshine v1 evidence bundle kind drift")
    if evidence.get("binding") != "positional":
        raise PrepareError("Moonshine v1 evidence binding drift")
    if evidence.get("hidden_size") != 288 or evidence.get("vocabulary_size") != 32768:
        raise PrepareError("Moonshine v1 evidence model dimensions drift")
    if evidence.get("cache_state_count") != 24:
        raise PrepareError("Moonshine v1 evidence cache count drift")
    if evidence.get("decoder_attention_heads") != 8 or evidence.get("decoder_head_dimension") != 36:
        raise PrepareError("Moonshine v1 evidence decoder cache geometry drift")

    artifacts = evidence.get("artifacts")
    if not isinstance(artifacts, list):
        raise PrepareError("Moonshine v1 evidence artifacts must be a list")
    names = [item.get("name") if isinstance(item, dict) else None for item in artifacts]
    if names != list(EXPECTED_ARTIFACTS):
        raise PrepareError("Moonshine v1 evidence artifact order/set drift")
    for index, item in enumerate(artifacts):
        if not isinstance(item, dict):
            raise PrepareError(f"Moonshine v1 evidence artifact {index} is invalid")
        size = item.get("size_bytes")
        sha = item.get("sha256")
        if not isinstance(size, int) or isinstance(size, bool) or size <= 0:
            raise PrepareError(f"Moonshine v1 evidence artifact {index} size is invalid")
        if not isinstance(sha, str) or len(sha) != 64 or any(ch not in "0123456789abcdef" for ch in sha):
            raise PrepareError(f"Moonshine v1 evidence artifact {index} sha256 is invalid")
    return artifacts


def prepare(
    snapshot_root: pathlib.Path,
    destination: pathlib.Path,
    manifest_path: pathlib.Path,
    evidence_path: pathlib.Path = DEFAULT_EVIDENCE,
) -> Dict[str, Any]:
    if not snapshot_root.is_dir():
        raise PrepareError(f"Moonshine reference snapshot root does not exist: {snapshot_root}")
    evidence = _load_json(evidence_path)
    if not isinstance(evidence, dict):
        raise PrepareError("Moonshine v1 evidence must contain an object")
    artifacts = _validate_evidence(evidence)

    verified: List[Dict[str, Any]] = []
    for item in artifacts:
        relative = pathlib.Path(str(item["name"]))
        source = snapshot_root / relative
        actual = _fingerprint(source)
        if actual["size_bytes"] != item["size_bytes"] or actual["sha256"] != item["sha256"]:
            raise PrepareError(f"Moonshine reference graph identity mismatch: {item['name']}")
        verified.append({
            "source": str(relative).replace("\\", "/"),
            "size_bytes": actual["size_bytes"],
            "sha256": actual["sha256"],
        })

    destination.mkdir(parents=True, exist_ok=True)
    staged_names = []
    for item in artifacts:
        relative = pathlib.Path(str(item["name"]))
        target = destination / relative.name
        shutil.copyfile(snapshot_root / relative, target)
        staged = _fingerprint(target)
        if staged["size_bytes"] != item["size_bytes"] or staged["sha256"] != item["sha256"]:
            raise PrepareError(f"staged Moonshine graph failed post-copy verification: {target.name}")
        staged_names.append(target.name)

    report = {
        "schema_version": 1,
        "model_id": EXPECTED_MODEL_ID,
        "revision": EXPECTED_REVISION,
        "bundle_kind": "moonshine-v1-four-graph",
        "binding": "positional",
        "destination": str(destination),
        "staged_graphs": staged_names,
        "verified_artifacts": verified,
        "token_decoder_required": True,
        "token_decoder_source": "moonshine-ai/moonshine-tiny tokenizer.json at its separately pinned source revision",
        "runtime_compatibility": "real-unity-import-and-quest-execution-required",
    }
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(json.dumps(report, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return report


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--snapshot-root", type=pathlib.Path, required=True)
    parser.add_argument("--destination", type=pathlib.Path, default=DEFAULT_DESTINATION)
    parser.add_argument("--manifest", type=pathlib.Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--evidence", type=pathlib.Path, default=DEFAULT_EVIDENCE)
    args = parser.parse_args()
    print(json.dumps(prepare(args.snapshot_root, args.destination, args.manifest, args.evidence), sort_keys=True))


if __name__ == "__main__":
    main()
