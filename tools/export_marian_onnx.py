#!/usr/bin/env python3
"""Offline-only, lock-bound Optimum ONNX export wrapper for the pinned Marian source snapshot.

The wrapper NEVER downloads model files. It requires a local source directory whose seven metadata/tokenizer files
match the committed evidence manifest and whose pytorch_model.bin matches the locked upstream LFS identity. Only
then can it execute the reviewed Optimum three-graph export. The exporter subprocess is forced into Hugging Face /
Transformers offline mode and the resulting graphs are immediately inspected with PhraseLayer's strict ONNX
contract.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import importlib.util
import json
import os
import pathlib
import shutil
import subprocess
from typing import Any, Callable, Dict, List, Mapping

MODEL_CANDIDATE_ID = "opus-mt-en-jap"
EXPECTED_TOOLCHAIN = {
    "optimum-onnx": "0.1.0",
    "optimum": "2.1.0",
    "transformers": "4.57.6",
    "torch": "2.9.1",
    "onnx": "1.19.1",
    "onnxruntime": "1.23.2",
    "sentencepiece": "0.2.2",
    "sacremoses": "0.1.1",
}


class MarianExportError(RuntimeError):
    pass


def _load_local_module(filename: str, module_name: str):
    path = pathlib.Path(__file__).with_name(filename)
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        raise MarianExportError(f"failed to load helper module: {filename}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _load_json(path: pathlib.Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise MarianExportError(f"failed to parse {path}: {exc}") from exc


def _sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _fingerprint(path: pathlib.Path) -> Dict[str, Any]:
    if not path.is_file():
        raise MarianExportError(f"missing local Marian source artifact: {path.name}")
    size = path.stat().st_size
    if size <= 0:
        raise MarianExportError(f"local Marian source artifact is empty: {path.name}")
    return {"name": path.name, "size_bytes": size, "sha256": _sha256(path)}


def _find_candidate(lock: Mapping[str, Any]) -> Mapping[str, Any]:
    candidates = lock.get("candidates")
    if not isinstance(candidates, list):
        raise MarianExportError("models.lock.json candidates must be a list")
    matches = [item for item in candidates if isinstance(item, dict) and item.get("id") == MODEL_CANDIDATE_ID]
    if len(matches) != 1:
        raise MarianExportError(f"models.lock.json must contain exactly one {MODEL_CANDIDATE_ID} candidate")
    return matches[0]


def validate_local_source_snapshot(
    source_dir: pathlib.Path,
    lock_path: pathlib.Path,
    repository_root: pathlib.Path,
) -> Dict[str, Any]:
    if not source_dir.is_dir():
        raise MarianExportError(f"local Marian source directory does not exist: {source_dir}")

    lock_validator = _load_local_module("validate_marian_lock_evidence.py", "validate_marian_lock_evidence")
    try:
        lock_report = lock_validator.validate_lock_evidence(lock_path, repository_root)
    except Exception as exc:
        raise MarianExportError(f"Marian lock/evidence validation failed: {exc}") from exc

    lock = _load_json(lock_path)
    candidate = _find_candidate(lock)
    evidence_path = repository_root / candidate["evidence_manifest"]
    evidence = _load_json(evidence_path)

    local_metadata = []
    for expected in evidence["artifacts"]:
        actual = _fingerprint(source_dir / expected["name"])
        if actual != expected:
            raise MarianExportError(
                "local Marian metadata/tokenizer artifact does not match pinned evidence: "
                + expected["name"]
            )
        local_metadata.append(actual)

    weight = candidate.get("source_weight_artifact")
    if not isinstance(weight, dict):
        raise MarianExportError("Marian lock is missing source_weight_artifact")
    weight_name = weight.get("artifact")
    if not isinstance(weight_name, str) or not weight_name:
        raise MarianExportError("Marian source_weight_artifact name is invalid")
    actual_weight = _fingerprint(source_dir / weight_name)
    expected_weight = {
        "name": weight_name,
        "size_bytes": weight.get("artifact_size_bytes"),
        "sha256": weight.get("artifact_sha256"),
    }
    if actual_weight != expected_weight:
        raise MarianExportError(
            "local Marian pytorch weight does not match locked upstream identity: " + weight_name
        )

    return {
        "model_id": candidate["upstream"],
        "revision": candidate["revision"],
        "license": candidate["license"],
        "metadata_artifacts": local_metadata,
        "weight_artifact": actual_weight,
        "lock_evidence": lock_report,
    }


def validate_export_toolchain(
    version_reader: Callable[[str], str] = importlib.metadata.version,
) -> Dict[str, str]:
    installed: Dict[str, str] = {}
    for distribution, expected in EXPECTED_TOOLCHAIN.items():
        try:
            actual = version_reader(distribution)
        except importlib.metadata.PackageNotFoundError as exc:
            raise MarianExportError(
                f"missing reviewed Marian export dependency: {distribution}=={expected}; "
                "install tools/requirements-marian-export.txt in an isolated environment"
            ) from exc
        if actual != expected:
            raise MarianExportError(
                f"Marian export toolchain drift: {distribution} expected {expected} but found {actual}"
            )
        installed[distribution] = actual
    return installed


def build_export_command(
    source_dir: pathlib.Path,
    output_dir: pathlib.Path,
    optimum_cli: str = "optimum-cli",
) -> List[str]:
    if not optimum_cli:
        raise MarianExportError("optimum-cli executable must not be empty")
    return [
        optimum_cli,
        "export",
        "onnx",
        "--model",
        str(source_dir),
        "--task",
        "text2text-generation-with-past",
        "--framework",
        "pt",
        "--dtype",
        "fp32",
        "--no-post-process",
        str(output_dir),
    ]


def _prepare_output_directory(output_dir: pathlib.Path) -> None:
    if output_dir.exists():
        if not output_dir.is_dir():
            raise MarianExportError(f"ONNX output path is not a directory: {output_dir}")
        if any(output_dir.iterdir()):
            raise MarianExportError(
                "ONNX output directory must be empty to prevent stale graph evidence: " + str(output_dir)
            )
    else:
        output_dir.mkdir(parents=True, exist_ok=False)


def execute_export(
    source_dir: pathlib.Path,
    output_dir: pathlib.Path,
    output_manifest: pathlib.Path,
    lock_path: pathlib.Path,
    repository_root: pathlib.Path,
    optimum_cli: str = "optimum-cli",
    runner: Callable[..., subprocess.CompletedProcess] = subprocess.run,
) -> Dict[str, Any]:
    source = validate_local_source_snapshot(source_dir, lock_path, repository_root)
    toolchain = validate_export_toolchain()
    _prepare_output_directory(output_dir)

    executable = shutil.which(optimum_cli) if pathlib.Path(optimum_cli).name == optimum_cli else optimum_cli
    if not executable:
        raise MarianExportError(
            "optimum-cli was not found; install tools/requirements-marian-export.txt in an isolated environment"
        )
    command = build_export_command(source_dir, output_dir, executable)
    environment = os.environ.copy()
    environment["HF_HUB_OFFLINE"] = "1"
    environment["TRANSFORMERS_OFFLINE"] = "1"
    environment["HF_DATASETS_OFFLINE"] = "1"
    environment["TOKENIZERS_PARALLELISM"] = "false"

    runner(command, check=True, env=environment)

    inspector = _load_local_module("inspect_marian_onnx_bundle.py", "inspect_marian_onnx_bundle")
    try:
        onnx = inspector.inspect_bundle(output_dir)
    except Exception as exc:
        raise MarianExportError(f"exported Marian ONNX bundle failed PhraseLayer contract inspection: {exc}") from exc

    manifest = {
        "schema_version": 1,
        "model_id": source["model_id"],
        "revision": source["revision"],
        "source": source,
        "toolchain": toolchain,
        "export": {
            "command": command,
            "offline_environment": {
                "HF_HUB_OFFLINE": "1",
                "TRANSFORMERS_OFFLINE": "1",
                "HF_DATASETS_OFFLINE": "1",
            },
            "task": "text2text-generation-with-past",
            "framework": "pt",
            "dtype": "fp32",
            "no_post_process": True,
        },
        "onnx": onnx,
        "runtime_compatibility": "unverified-real-unity-import-and-quest-execution-required",
    }
    output_manifest.parent.mkdir(parents=True, exist_ok=True)
    output_manifest.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-dir", type=pathlib.Path, required=True)
    parser.add_argument("--output-dir", type=pathlib.Path, required=True)
    parser.add_argument("--lock", type=pathlib.Path, default=pathlib.Path("models/models.lock.json"))
    parser.add_argument("--repository-root", type=pathlib.Path, default=pathlib.Path("."))
    parser.add_argument("--output-manifest", type=pathlib.Path)
    parser.add_argument("--optimum-cli", default="optimum-cli")
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args()

    source = validate_local_source_snapshot(args.source_dir, args.lock, args.repository_root)
    command = build_export_command(args.source_dir, args.output_dir, args.optimum_cli)
    if not args.execute:
        print(
            json.dumps(
                {
                    "status": "validated-dry-run",
                    "model_id": source["model_id"],
                    "revision": source["revision"],
                    "command": command,
                    "network_policy": "offline-only-on-execute",
                },
                sort_keys=True,
            )
        )
        return

    if args.output_manifest is None:
        parser.error("--output-manifest is required with --execute")
    manifest = execute_export(
        args.source_dir,
        args.output_dir,
        args.output_manifest,
        args.lock,
        args.repository_root,
        args.optimum_cli,
    )
    print(
        json.dumps(
            {
                "status": "exported-and-inspected",
                "revision": manifest["revision"],
                "graphs": sorted(manifest["onnx"]["graphs"].keys()),
                "manifest": str(args.output_manifest),
            },
            sort_keys=True,
        )
    )


if __name__ == "__main__":
    main()
