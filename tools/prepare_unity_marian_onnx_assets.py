#!/usr/bin/env python3
"""Verify a PhraseLayer Marian export manifest and stage its three ONNX graphs into Unity.

No network access is performed. The input bundle must already have been produced by
`export_marian_onnx.py`, which validates the exact source snapshot and pinned export toolchain.
This staging step re-verifies graph size/SHA from that manifest before and after copying so a
stale or substituted ONNX file cannot silently enter the Unity project.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import shutil
from typing import Any, Dict, Mapping

EXPECTED_MODEL_ID = "Helsinki-NLP/opus-mt-en-jap"
EXPECTED_REVISION = "a863894cdd2b80f3bc1c5966734aee9ffec207d1"
EXPECTED_GRAPHS = (
    "encoder_model.onnx",
    "decoder_model.onnx",
    "decoder_with_past_model.onnx",
)
DEFAULT_DESTINATION = pathlib.Path(
    "unity/PhraseLayer.Unity/Assets/LocalTranslationAssets/Marian"
)
DEFAULT_STAGING_MANIFEST = DEFAULT_DESTINATION / "marian-unity-staging.json"


class PrepareError(RuntimeError):
    pass


def _load_json(path: pathlib.Path) -> Mapping[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise PrepareError(f"failed to parse Marian export manifest {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise PrepareError("Marian export manifest must contain a JSON object")
    return value


def _sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _fingerprint(path: pathlib.Path) -> Dict[str, Any]:
    if not path.is_file():
        raise PrepareError(f"missing Marian ONNX graph: {path}")
    size = path.stat().st_size
    if size <= 0:
        raise PrepareError(f"Marian ONNX graph is empty: {path}")
    return {"size_bytes": size, "sha256": _sha256(path)}


def _expected_graphs(manifest: Mapping[str, Any]) -> Dict[str, Dict[str, Any]]:
    if manifest.get("schema_version") != 1:
        raise PrepareError("unsupported Marian export manifest schema")
    if manifest.get("model_id") != EXPECTED_MODEL_ID:
        raise PrepareError("Marian export model_id drift")
    if manifest.get("revision") != EXPECTED_REVISION:
        raise PrepareError("Marian export revision drift")

    export = manifest.get("export")
    if not isinstance(export, dict):
        raise PrepareError("Marian export manifest is missing export metadata")
    if export.get("task") != "text2text-generation-with-past":
        raise PrepareError("Marian export task drift")
    if export.get("framework") != "pt" or export.get("dtype") != "fp32":
        raise PrepareError("Marian export framework/dtype drift")
    if export.get("no_post_process") is not True:
        raise PrepareError("Marian export must preserve separate reviewed graphs")

    onnx = manifest.get("onnx")
    if not isinstance(onnx, dict):
        raise PrepareError("Marian export manifest is missing ONNX inspection evidence")
    graphs = onnx.get("graphs")
    if not isinstance(graphs, dict):
        raise PrepareError("Marian ONNX inspection evidence has no graph map")
    if set(graphs) != set(EXPECTED_GRAPHS):
        raise PrepareError("Marian ONNX graph set drift")

    expected: Dict[str, Dict[str, Any]] = {}
    for name in EXPECTED_GRAPHS:
        item = graphs.get(name)
        if not isinstance(item, dict):
            raise PrepareError(f"Marian ONNX graph evidence is invalid: {name}")
        size = item.get("size_bytes")
        sha = item.get("sha256")
        if not isinstance(size, int) or isinstance(size, bool) or size <= 0:
            raise PrepareError(f"Marian ONNX graph size is invalid: {name}")
        if (
            not isinstance(sha, str)
            or len(sha) != 64
            or any(ch not in "0123456789abcdef" for ch in sha)
        ):
            raise PrepareError(f"Marian ONNX graph sha256 is invalid: {name}")
        expected[name] = {"size_bytes": size, "sha256": sha}
    return expected


def prepare(
    export_dir: pathlib.Path,
    export_manifest: pathlib.Path,
    destination: pathlib.Path = DEFAULT_DESTINATION,
    staging_manifest: pathlib.Path = DEFAULT_STAGING_MANIFEST,
) -> Dict[str, Any]:
    if not export_dir.is_dir():
        raise PrepareError(f"Marian export directory does not exist: {export_dir}")
    manifest = _load_json(export_manifest)
    expected = _expected_graphs(manifest)

    verified = []
    for name in EXPECTED_GRAPHS:
        actual = _fingerprint(export_dir / name)
        if actual != expected[name]:
            raise PrepareError(f"Marian ONNX identity mismatch before Unity staging: {name}")
        verified.append({"name": name, **actual})

    destination.mkdir(parents=True, exist_ok=True)
    for name in EXPECTED_GRAPHS:
        target = destination / name
        shutil.copyfile(export_dir / name, target)
        actual = _fingerprint(target)
        if actual != expected[name]:
            raise PrepareError(f"Marian ONNX identity mismatch after Unity staging: {name}")

    report = {
        "schema_version": 1,
        "model_id": EXPECTED_MODEL_ID,
        "revision": EXPECTED_REVISION,
        "purpose": "unity-local-offline-translation-staging",
        "graphs": verified,
        "destination": str(destination),
        "weights_staged": False,
        "runtime_compatibility": "real-unity-import-and-quest-execution-required",
    }
    staging_manifest.parent.mkdir(parents=True, exist_ok=True)
    staging_manifest.write_text(
        json.dumps(report, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    return report


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--export-dir", type=pathlib.Path, required=True)
    parser.add_argument("--export-manifest", type=pathlib.Path, required=True)
    parser.add_argument("--destination", type=pathlib.Path, default=DEFAULT_DESTINATION)
    parser.add_argument("--staging-manifest", type=pathlib.Path, default=DEFAULT_STAGING_MANIFEST)
    args = parser.parse_args()
    report = prepare(args.export_dir, args.export_manifest, args.destination, args.staging_manifest)
    print(json.dumps(report, sort_keys=True))


if __name__ == "__main__":
    main()
