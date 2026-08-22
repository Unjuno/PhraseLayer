#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
from collections import Counter
from pathlib import Path
from typing import Any, Callable

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_LOCK = ROOT / "models" / "models.lock.json"
DEFAULT_OUTPUT = ROOT / "artifacts" / "translation-export" / "opus-mt-en-jap"
MANIFEST_NAME = "translation-export.manifest.json"
SCHEMA_VERSION = 1
EXPORT_TASK = "text2text-generation"


def load_translation_candidate(lock_path: Path = DEFAULT_LOCK) -> dict[str, Any]:
    manifest = json.loads(lock_path.read_text(encoding="utf-8"))
    candidates = manifest.get("candidates", [])
    candidate = next((item for item in candidates if item.get("purpose") == "translation-en-ja"), None)
    if candidate is None:
        raise ValueError("models.lock.json is missing the translation-en-ja candidate")

    required = {
        "id": "opus-mt-en-jap",
        "upstream": "Helsinki-NLP/opus-mt-en-jap",
        "revision": "a863894cdd2b80f3bc1c5966734aee9ffec207d1",
        "architecture": "marian",
        "tokenization": "SentencePiece",
        "runtime_target": "com.unity.ai.inference@2.2.1",
        "bundled": False,
    }
    for key, expected in required.items():
        actual = candidate.get(key)
        if actual != expected:
            raise ValueError(f"translation candidate {key} expected {expected!r}, found {actual!r}")
    if candidate.get("export_status") != "not-produced":
        raise ValueError("export probe expects candidate export_status=not-produced until generated artifacts are reviewed")
    return candidate


def ensure_empty_output_directory(output_dir: Path) -> None:
    if output_dir.exists():
        if not output_dir.is_dir():
            raise ValueError(f"output path exists and is not a directory: {output_dir}")
        if any(output_dir.iterdir()):
            raise ValueError(
                f"output directory must be empty so stale artifacts cannot be mistaken for this export: {output_dir}"
            )
    else:
        output_dir.mkdir(parents=True, exist_ok=False)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _dimension_to_json(dimension: Any) -> int | str | None:
    if getattr(dimension, "dim_value", 0):
        return int(dimension.dim_value)
    if getattr(dimension, "dim_param", ""):
        return str(dimension.dim_param)
    return None


def _value_info_to_json(value_info: Any) -> dict[str, Any]:
    tensor_type = value_info.type.tensor_type
    shape = []
    if tensor_type.HasField("shape"):
        shape = [_dimension_to_json(dimension) for dimension in tensor_type.shape.dim]
    return {
        "name": value_info.name,
        "element_type": int(tensor_type.elem_type),
        "shape": shape,
    }


def inspect_onnx(path: Path) -> dict[str, Any]:
    try:
        import onnx  # type: ignore
    except ImportError as exception:
        raise RuntimeError("ONNX inspection requires the 'onnx' package") from exception

    model = onnx.load(str(path), load_external_data=False)
    operator_counts = Counter(node.op_type for node in model.graph.node)
    external_locations: set[str] = set()
    for initializer in model.graph.initializer:
        if initializer.data_location != onnx.TensorProto.EXTERNAL:
            continue
        for item in initializer.external_data:
            if item.key == "location" and item.value:
                external_locations.add(item.value)

    return {
        "ir_version": int(model.ir_version),
        "opsets": [
            {"domain": item.domain or "ai.onnx", "version": int(item.version)}
            for item in model.opset_import
        ],
        "inputs": [_value_info_to_json(item) for item in model.graph.input],
        "outputs": [_value_info_to_json(item) for item in model.graph.output],
        "node_count": len(model.graph.node),
        "operator_counts": dict(sorted(operator_counts.items())),
        "external_data_locations": sorted(external_locations),
    }


def build_artifact_inventory(
    output_dir: Path,
    onnx_inspector: Callable[[Path], dict[str, Any]] = inspect_onnx,
) -> list[dict[str, Any]]:
    artifacts: list[dict[str, Any]] = []
    for path in sorted(item for item in output_dir.rglob("*") if item.is_file() and item.name != MANIFEST_NAME):
        relative = path.relative_to(output_dir).as_posix()
        record: dict[str, Any] = {
            "path": relative,
            "size_bytes": path.stat().st_size,
            "sha256": sha256_file(path),
            "kind": "onnx" if path.suffix.lower() == ".onnx" else "support",
        }
        if path.suffix.lower() == ".onnx":
            record["onnx"] = onnx_inspector(path)
        artifacts.append(record)
    return artifacts


def validate_export_outputs(artifacts: list[dict[str, Any]]) -> None:
    onnx_artifacts = [artifact for artifact in artifacts if artifact.get("kind") == "onnx"]
    if len(onnx_artifacts) < 2:
        raise ValueError(
            "expected a split encoder/decoder export with at least two discovered ONNX artifacts; "
            f"found {len(onnx_artifacts)}"
        )
    for artifact in onnx_artifacts:
        inspection = artifact.get("onnx")
        if not isinstance(inspection, dict):
            raise ValueError(f"ONNX artifact lacks graph inspection: {artifact.get('path')}")
        if not inspection.get("inputs") or not inspection.get("outputs"):
            raise ValueError(f"ONNX artifact must expose graph inputs and outputs: {artifact.get('path')}")


def resolved_package_versions() -> dict[str, str]:
    packages = ("optimum", "optimum-onnx", "transformers", "torch", "onnx", "sentencepiece")
    versions: dict[str, str] = {}
    for package in packages:
        try:
            versions[package] = importlib.metadata.version(package)
        except importlib.metadata.PackageNotFoundError:
            versions[package] = "not-installed"
    return versions


def build_manifest(candidate: dict[str, Any], artifacts: list[dict[str, Any]]) -> dict[str, Any]:
    validate_export_outputs(artifacts)
    return {
        "schema_version": SCHEMA_VERSION,
        "status": "unverified-real-unity-import-required",
        "source": {
            "id": candidate["id"],
            "upstream": candidate["upstream"],
            "revision": candidate["revision"],
            "architecture": candidate["architecture"],
            "tokenization": candidate["tokenization"],
            "license": candidate["license"],
        },
        "export": {
            "task": EXPORT_TASK,
            "framework": "pt",
            "monolith": False,
            "trust_remote_code": False,
            "do_validation": True,
            "dynamo": False,
            "runtime_target": candidate["runtime_target"],
            "package_versions": resolved_package_versions(),
        },
        "generation_contract": candidate["generation_contract"],
        "artifacts": artifacts,
    }


def write_manifest(output_dir: Path, manifest: dict[str, Any]) -> Path:
    path = output_dir / MANIFEST_NAME
    path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return path


def run_export(candidate: dict[str, Any], output_dir: Path) -> None:
    try:
        from optimum.exporters.onnx import main_export  # type: ignore
    except ImportError as exception:
        raise RuntimeError(
            "OPUS-MT export requires the Optimum ONNX exporter. Install the pinned probe dependencies first."
        ) from exception

    main_export(
        model_name_or_path=candidate["upstream"],
        output=output_dir,
        task=EXPORT_TASK,
        revision=candidate["revision"],
        framework="pt",
        monolith=False,
        trust_remote_code=False,
        do_validation=True,
        dynamo=False,
    )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Export the revision-pinned PhraseLayer OPUS-MT candidate and record the actual ONNX artifacts."
    )
    parser.add_argument("--lock", type=Path, default=DEFAULT_LOCK)
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument(
        "--inspect-only",
        action="store_true",
        help="Do not download/export; inspect and hash artifacts already present in an otherwise non-empty output directory.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    candidate = load_translation_candidate(args.lock)
    output_dir = args.output_dir.resolve()

    if args.inspect_only:
        if not output_dir.is_dir():
            raise SystemExit(f"inspect-only output directory does not exist: {output_dir}")
    else:
        ensure_empty_output_directory(output_dir)
        run_export(candidate, output_dir)

    artifacts = build_artifact_inventory(output_dir)
    manifest = build_manifest(candidate, artifacts)
    manifest_path = write_manifest(output_dir, manifest)
    print(f"PASS: inspected {len(artifacts)} exported artifact(s); manifest={manifest_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
