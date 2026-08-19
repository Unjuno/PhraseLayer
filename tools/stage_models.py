#!/usr/bin/env python3
"""Stage revision-pinned model artifacts outside the repository.

The lock file is the source of truth. Downloads are resolved at the exact
40-character revision recorded in models/models.lock.json. Primary and support
artifacts use the same integrity rules. Artifacts without both expected size and
SHA-256 are refused by default; use --allow-unverified-metadata only when
discovering metadata for a lock update.
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
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Iterable

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_LOCK = ROOT / "models" / "models.lock.json"
DEFAULT_OUTPUT = ROOT / "artifacts" / "models"
REVISION_RE = re.compile(r"^[0-9a-f]{40}$")
UPSTREAM_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")


class StageError(RuntimeError):
    pass


def load_candidates(lock_path: Path) -> list[dict]:
    payload = json.loads(lock_path.read_text(encoding="utf-8"))
    if payload.get("schema_version") != 2:
        raise StageError(f"unsupported lock schema: {payload.get('schema_version')!r}")
    candidates = payload.get("candidates")
    if not isinstance(candidates, list):
        raise StageError("models.lock.json candidates must be a list")
    return candidates


def validate_model_origin(model: dict) -> None:
    model_id = model.get("id", "<unknown>")
    upstream = model.get("upstream")
    revision = model.get("revision")

    if not isinstance(upstream, str) or UPSTREAM_RE.fullmatch(upstream) is None:
        raise StageError(f"{model_id}: unsupported upstream; expected owner/repo")
    if not isinstance(revision, str) or REVISION_RE.fullmatch(revision) is None:
        raise StageError(f"{model_id}: revision must be a full lowercase 40-character Git SHA")


def validate_artifact(model: dict, artifact: dict) -> None:
    validate_model_origin(model)
    model_id = model.get("id", "<unknown>")
    artifact_path_value = artifact.get("artifact")
    if not isinstance(artifact_path_value, str) or not artifact_path_value:
        raise StageError(f"{model_id}: artifact path is missing")

    artifact_path = Path(artifact_path_value)
    if artifact_path.is_absolute() or ".." in artifact_path.parts:
        raise StageError(f"{model_id}: artifact path must stay inside the upstream repository")

    expected_size = artifact.get("artifact_size_bytes")
    if expected_size is not None and (not isinstance(expected_size, int) or expected_size <= 0):
        raise StageError(f"{model_id}: artifact size must be null or a positive integer")

    expected_sha = artifact.get("artifact_sha256")
    if expected_sha is not None and (
        not isinstance(expected_sha, str) or SHA256_RE.fullmatch(expected_sha) is None
    ):
        raise StageError(f"{model_id}: artifact SHA-256 must be null or 64 lowercase hex characters")


def primary_artifact(model: dict) -> dict:
    artifact = {
        "kind": "primary",
        "purpose": model.get("purpose"),
        "artifact": model.get("artifact"),
        "artifact_size_bytes": model.get("artifact_size_bytes"),
        "artifact_sha256": model.get("artifact_sha256"),
    }
    validate_artifact(model, artifact)
    return artifact


def support_artifacts(model: dict) -> list[dict]:
    raw = model.get("support_artifacts", [])
    if raw is None:
        return []
    if not isinstance(raw, list):
        raise StageError(f"{model.get('id', '<unknown>')}: support_artifacts must be a list")

    result: list[dict] = []
    seen_paths: set[str] = set()
    for index, item in enumerate(raw):
        if not isinstance(item, dict):
            raise StageError(
                f"{model.get('id', '<unknown>')}: support_artifacts[{index}] must be an object"
            )
        artifact = dict(item)
        artifact["kind"] = "support"
        validate_artifact(model, artifact)
        path = artifact["artifact"]
        if path in seen_paths:
            raise StageError(f"{model.get('id', '<unknown>')}: duplicate support artifact path: {path}")
        seen_paths.add(path)
        result.append(artifact)
    return result


def iter_artifacts(model: dict, include_support: bool) -> list[dict]:
    artifacts = [primary_artifact(model)]
    if include_support:
        artifacts.extend(support_artifacts(model))
    return artifacts


def artifact_url(model: dict, artifact: dict) -> str:
    validate_artifact(model, artifact)
    upstream = model["upstream"]
    revision = model["revision"]
    artifact_path = urllib.parse.quote(artifact["artifact"], safe="/")
    return f"https://huggingface.co/{upstream}/resolve/{revision}/{artifact_path}?download=true"


def select_candidates(
    candidates: Iterable[dict], model_ids: list[str], purpose_prefix: str | None
) -> list[dict]:
    selected = list(candidates)
    if model_ids:
        wanted = set(model_ids)
        selected = [model for model in selected if model.get("id") in wanted]
        missing = sorted(wanted - {model.get("id") for model in selected})
        if missing:
            raise StageError("unknown model id(s): " + ", ".join(missing))
    if purpose_prefix:
        selected = [
            model
            for model in selected
            if str(model.get("purpose", "")).startswith(purpose_prefix)
        ]
    if not selected:
        raise StageError("selection matched no models")
    return selected


def file_metadata(path: Path) -> tuple[int, str]:
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as handle:
        while True:
            chunk = handle.read(1024 * 1024)
            if not chunk:
                break
            size += len(chunk)
            digest.update(chunk)
    return size, digest.hexdigest()


def verify_metadata(model: dict, artifact: dict, size: int, sha256: str) -> bool:
    expected_size = artifact.get("artifact_size_bytes")
    expected_sha = artifact.get("artifact_sha256")
    model_id = model.get("id", "<unknown>")
    artifact_path = artifact.get("artifact", "<unknown>")

    if expected_size is not None and size != expected_size:
        raise StageError(
            f"{model_id}:{artifact_path}: size mismatch: expected {expected_size}, got {size}"
        )
    if expected_sha is not None and sha256 != expected_sha:
        raise StageError(
            f"{model_id}:{artifact_path}: SHA-256 mismatch: expected {expected_sha}, got {sha256}"
        )

    return expected_size is not None and expected_sha is not None


def destination_for(model: dict, artifact: dict, output_dir: Path) -> Path:
    validate_artifact(model, artifact)
    return output_dir / model["id"] / Path(artifact["artifact"])


def print_artifact(model: dict, artifact: dict, output_dir: Path) -> None:
    validate_artifact(model, artifact)
    fully_pinned = (
        artifact.get("artifact_size_bytes") is not None
        and artifact.get("artifact_sha256") is not None
    )
    print(
        json.dumps(
            {
                "id": model["id"],
                "purpose": model.get("purpose"),
                "artifact_kind": artifact["kind"],
                "artifact_purpose": artifact.get("purpose"),
                "artifact": artifact["artifact"],
                "revision": model["revision"],
                "url": artifact_url(model, artifact),
                "destination": str(destination_for(model, artifact, output_dir)),
                "metadata_status": "verified-lock-metadata" if fully_pinned else "unverified-lock-metadata",
                "expected_size_bytes": artifact.get("artifact_size_bytes"),
                "expected_sha256": artifact.get("artifact_sha256"),
            },
            sort_keys=True,
        )
    )


def download_artifact(
    model: dict,
    artifact: dict,
    destination: Path,
    allow_unverified_metadata: bool,
) -> None:
    validate_artifact(model, artifact)
    expected_size = artifact.get("artifact_size_bytes")
    expected_sha = artifact.get("artifact_sha256")
    fully_pinned = expected_size is not None and expected_sha is not None

    if not fully_pinned and not allow_unverified_metadata:
        raise StageError(
            f"{model['id']}:{artifact['artifact']}: artifact size/SHA-256 are not both pinned; "
            "refusing download without --allow-unverified-metadata"
        )

    destination.parent.mkdir(parents=True, exist_ok=True)
    request = urllib.request.Request(
        artifact_url(model, artifact),
        headers={"User-Agent": "PhraseLayer-model-stager/2"},
    )

    fd, temp_name = tempfile.mkstemp(
        prefix=destination.name + ".", suffix=".part", dir=destination.parent
    )
    os.close(fd)
    temp_path = Path(temp_name)

    try:
        with urllib.request.urlopen(request, timeout=120) as response, temp_path.open("wb") as target:
            shutil.copyfileobj(response, target, length=1024 * 1024)

        size, sha256 = file_metadata(temp_path)
        verified = verify_metadata(model, artifact, size, sha256)
        os.replace(temp_path, destination)
        print(
            json.dumps(
                {
                    "id": model["id"],
                    "artifact_kind": artifact["kind"],
                    "artifact": artifact["artifact"],
                    "path": str(destination),
                    "size_bytes": size,
                    "sha256": sha256,
                    "status": "verified" if verified else "downloaded-unverified-metadata",
                },
                sort_keys=True,
            )
        )
    finally:
        if temp_path.exists():
            temp_path.unlink()


def verify_existing(
    model: dict,
    artifact: dict,
    destination: Path,
    allow_unverified_metadata: bool,
) -> None:
    if not destination.is_file():
        raise StageError(
            f"{model['id']}:{artifact['artifact']}: staged artifact is missing: {destination}"
        )
    size, sha256 = file_metadata(destination)
    verified = verify_metadata(model, artifact, size, sha256)
    if not verified and not allow_unverified_metadata:
        raise StageError(
            f"{model['id']}:{artifact['artifact']}: local file can be measured but lock metadata is incomplete; "
            "use --allow-unverified-metadata to report discovery values"
        )
    print(
        json.dumps(
            {
                "id": model["id"],
                "artifact_kind": artifact["kind"],
                "artifact": artifact["artifact"],
                "path": str(destination),
                "size_bytes": size,
                "sha256": sha256,
                "status": "verified" if verified else "measured-unverified-metadata",
            },
            sort_keys=True,
        )
    )


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", type=Path, default=DEFAULT_LOCK)
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--model", action="append", default=[], help="model id; repeat to select multiple")
    parser.add_argument("--purpose-prefix", help="select models whose purpose starts with this value")
    parser.add_argument(
        "--include-support",
        action="store_true",
        help="also list/download/verify revision-pinned support_artifacts",
    )
    parser.add_argument("--list", action="store_true", help="print resolved pinned URLs without network access")
    parser.add_argument("--verify-only", action="store_true", help="verify already-staged files; do not download")
    parser.add_argument(
        "--allow-unverified-metadata",
        action="store_true",
        help="allow artifacts whose size or SHA-256 is not yet locked; prints measured metadata",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    try:
        candidates = load_candidates(args.lock)
        selected = select_candidates(candidates, args.model, args.purpose_prefix)
        for model in selected:
            for artifact in iter_artifacts(model, args.include_support):
                if args.list:
                    print_artifact(model, artifact, args.output_dir)
                    continue
                destination = destination_for(model, artifact, args.output_dir)
                if args.verify_only:
                    verify_existing(
                        model,
                        artifact,
                        destination,
                        args.allow_unverified_metadata,
                    )
                else:
                    download_artifact(
                        model,
                        artifact,
                        destination,
                        args.allow_unverified_metadata,
                    )
        return 0
    except (OSError, ValueError, StageError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
