#!/usr/bin/env python3
"""Stage a parity-verified local translation export into git-ignored Unity Assets.

This command never downloads a model. Model/support files are copied only after size/SHA-256 verification.
When the locally generated managed tokenizer and tokenizer fixture manifests are supplied together they are also
hash-locked into the staging manifest as generated runtime artifacts, making the Quest bootstrap fail closed.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_UNITY_PROJECT = ROOT / "unity" / "PhraseLayer.Unity"
DEFAULT_LOCAL_ASSET_RELATIVE = Path("Assets") / "LocalTranslationAssets" / "OpusMtEnJap"
LOCAL_MANIFEST_NAME = "PhraseLayerTranslationAssets.manifest.json"
MANAGED_TOKENIZER_NAME = "phraselayer-sentencepiece-unigram-v1.txt"
TOKENIZER_FIXTURE_NAME = "phraselayer-tokenizer-fixtures-v1.txt"
EXPECTED_MODEL_ID = "Helsinki-NLP/opus-mt-en-jap"
EXPECTED_REVISION = "a863894cdd2b80f3bc1c5966734aee9ffec207d1"
EXPECTED_RUNTIME_STATUS = "unverified-real-unity-import-required"
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")


class PrepareTranslationError(RuntimeError):
    pass


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def safe_relative_path(value: Any) -> Path:
    if not isinstance(value, str) or not value:
        raise PrepareTranslationError("probe artifact path must be a non-empty string")
    path = Path(value)
    if path.is_absolute() or ".." in path.parts:
        raise PrepareTranslationError(f"probe artifact path escapes export root: {value!r}")
    if not path.parts or any(part in ("", ".") for part in path.parts):
        raise PrepareTranslationError(f"probe artifact path is not canonical: {value!r}")
    return path


def load_and_validate_report(report_path: Path) -> dict[str, Any]:
    if not report_path.is_file():
        raise PrepareTranslationError(f"translation probe report is missing: {report_path}")
    try:
        report = json.loads(report_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise PrepareTranslationError(f"translation probe report is not valid JSON: {error}") from error
    if not isinstance(report, dict):
        raise PrepareTranslationError("translation probe report must be a JSON object")

    if report.get("model_id") != EXPECTED_MODEL_ID:
        raise PrepareTranslationError(f"translation probe model_id must be {EXPECTED_MODEL_ID!r}; found {report.get('model_id')!r}")
    if report.get("revision") != EXPECTED_REVISION:
        raise PrepareTranslationError(f"translation probe revision must be {EXPECTED_REVISION}; found {report.get('revision')!r}")
    if report.get("status") != "pass":
        raise PrepareTranslationError("translation probe must have status=pass before Unity staging")
    if report.get("runtime_status") != EXPECTED_RUNTIME_STATUS:
        raise PrepareTranslationError("translation probe must remain unverified for real Unity import while staging local assets")
    parity = report.get("parity")
    if not isinstance(parity, dict) or parity.get("exact") is not True:
        raise PrepareTranslationError("translation probe must prove token-exact/text-exact reference parity")

    files = report.get("files")
    if not isinstance(files, list) or not files:
        raise PrepareTranslationError("translation probe report contains no exported files")

    seen: set[str] = set()
    onnx_count = 0
    for item in files:
        if not isinstance(item, dict):
            raise PrepareTranslationError("translation probe file entry must be an object")
        relative = safe_relative_path(item.get("path"))
        canonical = relative.as_posix()
        if canonical in seen:
            raise PrepareTranslationError(f"duplicate translation probe artifact path: {canonical}")
        seen.add(canonical)
        size = item.get("size_bytes")
        digest = item.get("sha256")
        if not isinstance(size, int) or size < 0:
            raise PrepareTranslationError(f"invalid size for translation artifact {canonical}")
        if not isinstance(digest, str) or SHA256_PATTERN.fullmatch(digest) is None:
            raise PrepareTranslationError(f"invalid SHA-256 for translation artifact {canonical}")
        if relative.suffix.lower() == ".onnx":
            onnx_count += 1
            inspection = item.get("onnx")
            if not isinstance(inspection, dict) or not inspection.get("inputs") or not inspection.get("outputs"):
                raise PrepareTranslationError(f"ONNX artifact lacks input/output metadata: {canonical}")
    if onnx_count < 2:
        raise PrepareTranslationError(f"translation probe must describe at least encoder and decoder ONNX artifacts; found {onnx_count}")
    return report


def validate_unity_destination(unity_project: Path, relative: Path) -> Path:
    if relative.is_absolute() or ".." in relative.parts:
        raise PrepareTranslationError("Unity translation asset path must be relative and cannot contain '..'")
    if not relative.parts or relative.parts[0] != "Assets":
        raise PrepareTranslationError("Unity translation asset path must live under the project's Assets directory")
    project = unity_project.resolve()
    assets = (project / "Assets").resolve()
    if not assets.is_dir():
        raise PrepareTranslationError(f"Unity project Assets directory is missing: {assets}")
    destination = (project / relative).resolve()
    try:
        destination.relative_to(assets)
    except ValueError as error:
        raise PrepareTranslationError("Unity translation asset destination escaped Assets") from error
    return destination


def verify_source(export_root: Path, relative: Path, expected_size: int, expected_sha: str) -> Path:
    root = export_root.resolve()
    source = (root / relative).resolve()
    try:
        source.relative_to(root)
    except ValueError as error:
        raise PrepareTranslationError(f"translation artifact escaped export root: {relative}") from error
    if not source.is_file():
        raise PrepareTranslationError(f"translation export artifact is missing: {source}")
    size = source.stat().st_size
    digest = sha256_file(source)
    if size != expected_size or digest != expected_sha:
        raise PrepareTranslationError(
            f"translation artifact identity mismatch for {relative.as_posix()}: expected {expected_size}/{expected_sha}, got {size}/{digest}"
        )
    return source


def replace_directory_atomically(staged: Path, destination: Path) -> None:
    backup = destination.with_name(destination.name + ".previous")
    if backup.exists():
        shutil.rmtree(backup)
    had_previous = destination.exists()
    if had_previous:
        os.replace(destination, backup)
    try:
        os.replace(staged, destination)
    except Exception:
        if had_previous and backup.exists() and not destination.exists():
            os.replace(backup, destination)
        raise
    else:
        if backup.exists():
            shutil.rmtree(backup)


def copy_generated(source: Path, staged: Path, name: str) -> dict[str, Any]:
    if not source.is_file():
        raise PrepareTranslationError(f"generated translation runtime artifact is missing: {source}")
    target = staged / name
    shutil.copyfile(source, target)
    source_size = source.stat().st_size
    source_sha = sha256_file(source)
    if target.stat().st_size != source_size or sha256_file(target) != source_sha:
        raise PrepareTranslationError(f"generated translation runtime artifact copy verification failed: {name}")
    return {"asset_path": name, "size_bytes": source_size, "sha256": source_sha, "kind": "generated"}


def prepare(
    report_path: Path,
    export_root: Path,
    unity_project: Path,
    local_asset_relative: Path = DEFAULT_LOCAL_ASSET_RELATIVE,
    managed_tokenizer_manifest: Path | None = None,
    tokenizer_fixture_manifest: Path | None = None,
) -> dict[str, Any]:
    report = load_and_validate_report(report_path)
    if not export_root.is_dir():
        raise PrepareTranslationError(f"translation export root is missing: {export_root}")
    if (managed_tokenizer_manifest is None) != (tokenizer_fixture_manifest is None):
        raise PrepareTranslationError("managed tokenizer and tokenizer fixture manifests must be supplied together")

    destination = validate_unity_destination(unity_project, local_asset_relative)
    destination.parent.mkdir(parents=True, exist_ok=True)
    staged_path = Path(tempfile.mkdtemp(prefix=destination.name + ".staging-", dir=destination.parent))
    copied: list[dict[str, Any]] = []
    try:
        for item in report["files"]:
            relative = safe_relative_path(item["path"])
            source = verify_source(export_root, relative, item["size_bytes"], item["sha256"])
            target = staged_path / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
            copied_size = target.stat().st_size
            copied_sha = sha256_file(target)
            if copied_size != item["size_bytes"] or copied_sha != item["sha256"]:
                raise PrepareTranslationError(f"post-copy verification failed for {relative.as_posix()}")
            copied.append({
                "asset_path": relative.as_posix(),
                "size_bytes": copied_size,
                "sha256": copied_sha,
                "kind": "onnx" if relative.suffix.lower() == ".onnx" else "support",
            })

        bootstrap_ready = managed_tokenizer_manifest is not None
        if bootstrap_ready:
            copied.append(copy_generated(managed_tokenizer_manifest, staged_path, MANAGED_TOKENIZER_NAME))
            copied.append(copy_generated(tokenizer_fixture_manifest, staged_path, TOKENIZER_FIXTURE_NAME))

        local_manifest = {
            "schema_version": 1,
            "purpose": "PhraseLayer local Unity translation asset staging",
            "git_policy": "local-only; directory is ignored and model binaries are not committed",
            "model_id": report["model_id"],
            "revision": report["revision"],
            "probe_report_sha256": sha256_file(report_path),
            "reference_parity_exact": True,
            "runtime_status": EXPECTED_RUNTIME_STATUS,
            "runtime_bootstrap_ready": bootstrap_ready,
            "files": copied,
        }
        (staged_path / LOCAL_MANIFEST_NAME).write_text(
            json.dumps(local_manifest, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        replace_directory_atomically(staged_path, destination)
        staged_path = Path()
        return {
            "destination": str(destination),
            "manifest": str(destination / LOCAL_MANIFEST_NAME),
            "file_count": len(copied),
            "onnx_count": sum(1 for item in copied if item["kind"] == "onnx"),
            "runtime_bootstrap_ready": bootstrap_ready,
        }
    finally:
        if staged_path != Path() and staged_path.exists():
            shutil.rmtree(staged_path)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--export-root", type=Path, required=True)
    parser.add_argument("--unity-project", type=Path, default=DEFAULT_UNITY_PROJECT)
    parser.add_argument("--local-asset-relative", type=Path, default=DEFAULT_LOCAL_ASSET_RELATIVE)
    parser.add_argument("--managed-tokenizer-manifest", type=Path)
    parser.add_argument("--tokenizer-fixture-manifest", type=Path)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    try:
        result = prepare(
            args.report,
            args.export_root,
            args.unity_project,
            args.local_asset_relative,
            args.managed_tokenizer_manifest,
            args.tokenizer_fixture_manifest,
        )
        print(json.dumps(result, sort_keys=True))
        return 0
    except (OSError, ValueError, PrepareTranslationError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
