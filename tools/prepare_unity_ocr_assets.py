#!/usr/bin/env python3
"""Verify staged PP-OCR artifacts, derive the pinned dictionary, and copy them into Unity Assets.

This tool is intentionally local-only: it never downloads model files. Run `tools/stage_models.py`
first in a network-enabled environment, then use this command to verify all staged bytes against
`models/models.lock.json`, regenerate the recognizer dictionary from the pinned `inference.yml`,
and atomically populate the git-ignored Unity `Assets/LocalOcrAssets/PaddleOCR` directory.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import sys
import tempfile
from pathlib import Path

import extract_ppocr_dictionary
import stage_models

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_LOCK = ROOT / "models" / "models.lock.json"
DEFAULT_STAGED_ROOT = ROOT / "artifacts" / "models"
DEFAULT_GENERATED_ROOT = ROOT / "artifacts" / "generated" / "pp-ocrv6-tiny-rec"
DEFAULT_UNITY_PROJECT = ROOT / "unity" / "PhraseLayer.Unity"
DEFAULT_LOCAL_ASSET_RELATIVE = Path("Assets") / "LocalOcrAssets" / "PaddleOCR"
LOCAL_MANIFEST_NAME = "PhraseLayerOcrAssets.manifest.json"


class PrepareError(RuntimeError):
    pass


def load_model(lock_path: Path, model_id: str) -> dict:
    candidates = stage_models.load_candidates(lock_path)
    matches = [model for model in candidates if model.get("id") == model_id]
    if len(matches) != 1:
        raise PrepareError(
            f"expected exactly one lock entry for {model_id!r}; found {len(matches)}"
        )
    return matches[0]


def verify_file_against_artifact(model: dict, artifact: dict, path: Path) -> dict:
    if not path.is_file():
        raise PrepareError(f"staged artifact is missing: {path}")
    size, sha256 = stage_models.file_metadata(path)
    verified = stage_models.verify_metadata(model, artifact, size, sha256)
    if not verified:
        raise PrepareError(
            f"{model['id']}:{artifact['artifact']}: lock metadata must include size and SHA-256"
        )
    return {
        "path": str(path),
        "size_bytes": size,
        "sha256": sha256,
    }


def primary_artifact(model: dict) -> dict:
    return stage_models.primary_artifact(model)


def support_artifact(model: dict, artifact_path: str) -> dict:
    matches = [
        artifact
        for artifact in stage_models.support_artifacts(model)
        if artifact.get("artifact") == artifact_path
    ]
    if len(matches) != 1:
        raise PrepareError(
            f"{model['id']}: expected exactly one support artifact {artifact_path!r}; found {len(matches)}"
        )
    return matches[0]


def validate_unity_destination(unity_project: Path, relative: Path) -> Path:
    if relative.is_absolute() or ".." in relative.parts:
        raise PrepareError("Unity local asset path must be relative and cannot contain '..'")
    if not relative.parts or relative.parts[0] != "Assets":
        raise PrepareError("Unity local asset path must live under the project's Assets directory")

    project = unity_project.resolve()
    assets = (project / "Assets").resolve()
    if not assets.is_dir():
        raise PrepareError(f"Unity project Assets directory is missing: {assets}")

    destination = (project / relative).resolve()
    try:
        destination.relative_to(assets)
    except ValueError as error:
        raise PrepareError("Unity local asset destination escaped the Assets directory") from error
    return destination


def atomic_copy(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(
        prefix=destination.name + ".",
        suffix=".tmp",
        dir=destination.parent,
    )
    os.close(fd)
    temp_path = Path(temp_name)
    try:
        shutil.copyfile(source, temp_path)
        os.replace(temp_path, destination)
    finally:
        if temp_path.exists():
            temp_path.unlink()


def copy_and_verify(source: Path, destination: Path, expected_size: int, expected_sha: str) -> dict:
    atomic_copy(source, destination)
    size, sha256 = stage_models.file_metadata(destination)
    if size != expected_size or sha256 != expected_sha:
        raise PrepareError(
            f"post-copy verification failed for {destination}: expected "
            f"{expected_size}/{expected_sha}, got {size}/{sha256}"
        )
    return {
        "asset_path": destination.name,
        "size_bytes": size,
        "sha256": sha256,
    }


def build_local_manifest(
    detector: dict,
    recognizer: dict,
    detector_file: dict,
    recognizer_file: dict,
    dictionary_file: dict,
    dictionary_manifest_file: dict,
) -> dict:
    dictionary_contract = recognizer["recognition_dictionary"]
    return {
        "schema_version": 1,
        "purpose": "PhraseLayer local Unity OCR asset staging",
        "git_policy": "local-only; directory is ignored and model binaries are not committed",
        "detector": {
            "model_id": detector["id"],
            "upstream": detector["upstream"],
            "revision": detector["revision"],
            **detector_file,
        },
        "recognizer": {
            "model_id": recognizer["id"],
            "upstream": recognizer["upstream"],
            "revision": recognizer["revision"],
            **recognizer_file,
        },
        "dictionary": {
            "source_artifact": dictionary_contract["source_artifact"],
            "raw_token_count": dictionary_contract["raw_token_count"],
            "effective_token_count": dictionary_contract["effective_token_count"],
            "use_space_char": dictionary_contract["use_space_char"],
            **dictionary_file,
        },
        "dictionary_manifest": dictionary_manifest_file,
    }


def prepare(
    lock_path: Path,
    staged_root: Path,
    generated_root: Path,
    unity_project: Path,
    local_asset_relative: Path,
) -> dict:
    detector = load_model(lock_path, "pp-ocrv6-tiny-det")
    recognizer = load_model(lock_path, "pp-ocrv6-tiny-rec")

    detector_artifact = primary_artifact(detector)
    recognizer_artifact = primary_artifact(recognizer)
    dictionary_contract = recognizer.get("recognition_dictionary")
    if not isinstance(dictionary_contract, dict):
        raise PrepareError("recognizer recognition_dictionary contract is missing")

    source_artifact_name = dictionary_contract.get("source_artifact")
    if not isinstance(source_artifact_name, str) or not source_artifact_name:
        raise PrepareError("recognizer dictionary source_artifact is missing")
    source_artifact = support_artifact(recognizer, source_artifact_name)

    detector_source = staged_root / detector["id"] / detector_artifact["artifact"]
    recognizer_source = staged_root / recognizer["id"] / recognizer_artifact["artifact"]
    dictionary_source = staged_root / recognizer["id"] / source_artifact["artifact"]

    detector_verified = verify_file_against_artifact(detector, detector_artifact, detector_source)
    recognizer_verified = verify_file_against_artifact(recognizer, recognizer_artifact, recognizer_source)
    verify_file_against_artifact(recognizer, source_artifact, dictionary_source)

    dictionary_path, dictionary_manifest_path, generated_manifest = (
        extract_ppocr_dictionary.export_dictionary(
            lock_path,
            recognizer["id"],
            dictionary_source,
            generated_root,
        )
    )

    expected_dictionary_size = dictionary_contract["generated_artifact_size_bytes"]
    expected_dictionary_sha = dictionary_contract["generated_artifact_sha256"]
    generated_size, generated_sha = stage_models.file_metadata(dictionary_path)
    if generated_size != expected_dictionary_size or generated_sha != expected_dictionary_sha:
        raise PrepareError("generated dictionary does not match the lock after extraction")

    destination = validate_unity_destination(unity_project, local_asset_relative)
    destination.mkdir(parents=True, exist_ok=True)

    detector_destination = destination / "detector.onnx"
    recognizer_destination = destination / "recognizer.onnx"
    dictionary_destination = destination / dictionary_contract["generated_artifact"]
    dictionary_manifest_destination = destination / dictionary_contract["generated_manifest"]

    detector_file = copy_and_verify(
        detector_source,
        detector_destination,
        detector_verified["size_bytes"],
        detector_verified["sha256"],
    )
    recognizer_file = copy_and_verify(
        recognizer_source,
        recognizer_destination,
        recognizer_verified["size_bytes"],
        recognizer_verified["sha256"],
    )
    dictionary_file = copy_and_verify(
        dictionary_path,
        dictionary_destination,
        expected_dictionary_size,
        expected_dictionary_sha,
    )

    manifest_bytes = dictionary_manifest_path.read_bytes()
    manifest_size, manifest_sha = stage_models.file_metadata(dictionary_manifest_path)
    atomic_copy(dictionary_manifest_path, dictionary_manifest_destination)
    copied_manifest_size, copied_manifest_sha = stage_models.file_metadata(
        dictionary_manifest_destination
    )
    if copied_manifest_size != manifest_size or copied_manifest_sha != manifest_sha:
        raise PrepareError("post-copy verification failed for generated dictionary manifest")
    dictionary_manifest_file = {
        "asset_path": dictionary_manifest_destination.name,
        "size_bytes": manifest_size,
        "sha256": manifest_sha,
        "generated_contract_sha256": generated_manifest["generated_sha256"],
    }

    local_manifest = build_local_manifest(
        detector,
        recognizer,
        detector_file,
        recognizer_file,
        dictionary_file,
        dictionary_manifest_file,
    )
    local_manifest_path = destination / LOCAL_MANIFEST_NAME
    local_manifest_path.write_text(
        json.dumps(local_manifest, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )

    return {
        "destination": str(destination),
        "local_manifest": str(local_manifest_path),
        "detector": detector_file,
        "recognizer": recognizer_file,
        "dictionary": dictionary_file,
        "dictionary_manifest": dictionary_manifest_file,
    }


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", type=Path, default=DEFAULT_LOCK)
    parser.add_argument("--staged-root", type=Path, default=DEFAULT_STAGED_ROOT)
    parser.add_argument("--generated-root", type=Path, default=DEFAULT_GENERATED_ROOT)
    parser.add_argument("--unity-project", type=Path, default=DEFAULT_UNITY_PROJECT)
    parser.add_argument(
        "--local-asset-relative",
        type=Path,
        default=DEFAULT_LOCAL_ASSET_RELATIVE,
        help="relative path under the Unity project; must start with Assets/",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    try:
        result = prepare(
            args.lock,
            args.staged_root,
            args.generated_root,
            args.unity_project,
            args.local_asset_relative,
        )
        print(json.dumps(result, sort_keys=True))
        return 0
    except (OSError, ValueError, stage_models.StageError, extract_ppocr_dictionary.DictionaryExportError, PrepareError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
