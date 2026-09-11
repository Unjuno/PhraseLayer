#!/usr/bin/env python3
"""Stage a fully validated local Marian ONNX/tokenizer/reference bundle into git-ignored Unity assets.

This tool never downloads or exports model data. The caller supplies the exact pinned source snapshot, an already
exported three-graph ONNX bundle, and a trusted reference fixture generated from that source snapshot. Source weight
identity, metadata, ONNX graph contracts, reference revision/weight identity, and staged file hashes are all checked
before Unity sees the assets. The 273 MB PyTorch source weight is never copied into Unity.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import shutil
from pathlib import Path
from typing import Any

MODEL_FILES = (
    "encoder_model.onnx",
    "decoder_model.onnx",
    "decoder_with_past_model.onnx",
)
TOKENIZER_FILES = (
    ("source.spm", "source.spm.bytes"),
    ("target.spm", "target.spm.bytes"),
    ("vocab.json", "vocab.json"),
)
EXPECTED_MODEL_ID = "Helsinki-NLP/opus-mt-en-jap"


class PrepareMarianUnityError(RuntimeError):
    pass


def _load_local_module(filename: str, module_name: str):
    path = Path(__file__).with_name(filename)
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        raise PrepareMarianUnityError(f"failed to load helper module: {filename}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def fingerprint(path: Path) -> dict[str, Any]:
    if not path.is_file() or path.stat().st_size <= 0:
        raise PrepareMarianUnityError(f"missing or empty Marian staging input: {path}")
    return {
        "file": path.name,
        "size_bytes": path.stat().st_size,
        "sha256": sha256(path),
    }


def _read_json(path: Path) -> dict[str, Any]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise PrepareMarianUnityError(f"failed to parse JSON {path}: {exc}") from exc
    if not isinstance(data, dict):
        raise PrepareMarianUnityError(f"expected a JSON object: {path}")
    return data


def _require_under(path: Path, root: Path, label: str) -> Path:
    resolved = path.resolve()
    resolved_root = root.resolve()
    try:
        resolved.relative_to(resolved_root)
    except ValueError as exc:
        raise PrepareMarianUnityError(f"{label} must stay under {resolved_root}: {resolved}") from exc
    return resolved


def _copy_verified(source: Path, target: Path) -> dict[str, Any]:
    source_fp = fingerprint(source)
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)
    target_fp = fingerprint(target)
    if source_fp["size_bytes"] != target_fp["size_bytes"] or source_fp["sha256"] != target_fp["sha256"]:
        raise PrepareMarianUnityError(f"staged Marian asset bytes changed: {source.name}")
    return target_fp


def stage(
    *,
    source_dir: Path,
    onnx_dir: Path,
    reference_fixture: Path,
    repository_root: Path,
    unity_project: Path,
) -> dict[str, Any]:
    repository_root = repository_root.resolve()
    unity_project = unity_project.resolve()
    assets_root = unity_project / "Assets"
    if not assets_root.is_dir():
        raise PrepareMarianUnityError(f"Unity Assets directory does not exist: {assets_root}")

    export = _load_local_module("export_marian_onnx.py", "export_marian_onnx_for_unity_stage")
    inspector = _load_local_module("inspect_marian_onnx_bundle.py", "inspect_marian_onnx_bundle_for_unity_stage")
    source = export.validate_local_source_snapshot(
        source_dir.resolve(),
        repository_root / "models/models.lock.json",
        repository_root,
    )
    if source["model_id"] != EXPECTED_MODEL_ID:
        raise PrepareMarianUnityError(f"unexpected Marian model id: {source['model_id']}")

    try:
        onnx_report = inspector.inspect_bundle(onnx_dir.resolve())
    except Exception as exc:
        raise PrepareMarianUnityError(f"Marian ONNX contract inspection failed: {exc}") from exc
    graph_names = set(onnx_report.get("graphs", {}).keys())
    if graph_names != set(MODEL_FILES):
        raise PrepareMarianUnityError(
            "Marian ONNX bundle must contain exactly the reviewed three graphs; observed=" +
            ",".join(sorted(graph_names))
        )

    reference = _read_json(reference_fixture.resolve())
    if reference.get("purpose") != "phrase-layer-marian-greedy-reference":
        raise PrepareMarianUnityError("reference fixture purpose is not PhraseLayer Marian greedy reference")
    if reference.get("model_id") != source["model_id"]:
        raise PrepareMarianUnityError("reference fixture model id does not match validated Marian source")
    if reference.get("revision") != source["revision"]:
        raise PrepareMarianUnityError("reference fixture revision does not match validated Marian source")
    if reference.get("source_weight_sha256") != source["weight_artifact"]["sha256"]:
        raise PrepareMarianUnityError("reference fixture source weight hash does not match validated Marian source")
    samples = reference.get("samples")
    if not isinstance(samples, list) or len(samples) < 3:
        raise PrepareMarianUnityError("reference fixture must contain at least three translation samples")

    model_root = _require_under(assets_root / "LocalTranslationAssets/Marian", assets_root, "model destination")
    resource_root = _require_under(assets_root / "Resources/LocalTranslationAssets", assets_root, "tokenizer destination")
    evidence_root = _require_under(assets_root / "LocalTranslationAssets", assets_root, "evidence destination")

    if model_root.exists():
        shutil.rmtree(model_root)
    model_root.mkdir(parents=True, exist_ok=False)
    resource_root.mkdir(parents=True, exist_ok=True)

    staged_models = []
    for name in MODEL_FILES:
        staged_models.append(_copy_verified(onnx_dir / name, model_root / name))

    staged_tokenizer = []
    for source_name, target_name in TOKENIZER_FILES:
        staged_tokenizer.append(_copy_verified(source_dir / source_name, resource_root / target_name))

    staged_reference = _copy_verified(reference_fixture, resource_root / "marian-reference.json")

    # Remove a stale source weight if a prior manual experiment placed one below the local Unity asset root.
    for forbidden_name in ("pytorch_model.bin", "model.safetensors"):
        for stale in (assets_root / "LocalTranslationAssets").rglob(forbidden_name):
            stale.unlink()
        for stale in resource_root.rglob(forbidden_name):
            stale.unlink()

    evidence = {
        "schema_version": 1,
        "purpose": "phrase-layer-unity-local-marian-assets",
        "model_id": source["model_id"],
        "revision": source["revision"],
        "license": source["license"],
        "redistribution_review": "pending",
        "source_weight": source["weight_artifact"],
        "source_weight_copied_to_unity": False,
        "onnx_contract_inspected": True,
        "onnx_graphs": staged_models,
        "tokenizer_assets": staged_tokenizer,
        "reference_fixture": staged_reference,
        "reference_sample_count": len(samples),
        "unity_model_root": "Assets/LocalTranslationAssets/Marian",
        "unity_tokenizer_resource_root": "LocalTranslationAssets",
        "runtime_target": "com.unity.ai.inference@2.2.1",
        "runtime_compatibility": "unverified-real-unity-parity-required",
    }
    evidence_root.mkdir(parents=True, exist_ok=True)
    evidence_path = evidence_root / "PhraseLayerMarianAssets.manifest.json"
    evidence_path.write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return evidence


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-dir", type=Path, required=True)
    parser.add_argument("--onnx-dir", type=Path, required=True)
    parser.add_argument("--reference-fixture", type=Path, required=True)
    parser.add_argument("--repository-root", type=Path, default=Path("."))
    parser.add_argument("--unity-project", type=Path, default=Path("unity/PhraseLayer.Unity"))
    args = parser.parse_args()

    evidence = stage(
        source_dir=args.source_dir,
        onnx_dir=args.onnx_dir,
        reference_fixture=args.reference_fixture,
        repository_root=args.repository_root,
        unity_project=args.unity_project,
    )
    print(
        json.dumps(
            {
                "status": "pass",
                "purpose": evidence["purpose"],
                "revision": evidence["revision"],
                "onnx_graph_count": len(evidence["onnx_graphs"]),
                "reference_sample_count": evidence["reference_sample_count"],
                "source_weight_copied_to_unity": evidence["source_weight_copied_to_unity"],
            },
            sort_keys=True,
        )
    )


if __name__ == "__main__":
    main()
