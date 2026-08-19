#!/usr/bin/env python3
"""Stage revision-pinned model artifacts outside the repository.

The lock file is the source of truth. Downloads are resolved at the exact
40-character revision recorded in models/models.lock.json. Candidates without
both expected size and SHA-256 are refused by default; use
--allow-unverified-metadata only when discovering metadata for a lock update.
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


def validate_download_candidate(model: dict) -> None:
    model_id = model.get("id", "<unknown>")
    upstream = model.get("upstream")
    revision = model.get("revision")
    artifact = model.get("artifact")

    if not isinstance(upstream, str) or UPSTREAM_RE.fullmatch(upstream) is None:
        raise StageError(f"{model_id}: unsupported upstream; expected owner/repo")
    if not isinstance(revision, str) or REVISION_RE.fullmatch(revision) is None:
        raise StageError(f"{model_id}: revision must be a full lowercase 40-character Git SHA")
    if not isinstance(artifact, str) or not artifact:
        raise StageError(f"{model_id}: artifact path is missing")

    artifact_path = Path(artifact)
    if artifact_path.is_absolute() or ".." in artifact_path.parts:
        raise StageError(f"{model_id}: artifact path must stay inside the upstream repository")


def artifact_url(model: dict) -> str:
    validate_download_candidate(model)
    upstream = model["upstream"]
    revision = model["revision"]
    artifact = urllib.parse.quote(model["artifact"], safe="/")
    return f"https://huggingface.co/{upstream}/resolve/{revision}/{artifact}?download=true"


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


def verify_metadata(model: dict, size: int, sha256: str) -> bool:
    expected_size = model.get("artifact_size_bytes")
    expected_sha = model.get("artifact_sha256")
    model_id = model.get("id", "<unknown>")

    if expected_size is not None and size != expected_size:
        raise StageError(f"{model_id}: size mismatch: expected {expected_size}, got {size}")
    if expected_sha is not None and sha256 != expected_sha:
        raise StageError(f"{model_id}: SHA-256 mismatch: expected {expected_sha}, got {sha256}")

    return expected_size is not None and expected_sha is not None


def destination_for(model: dict, output_dir: Path) -> Path:
    validate_download_candidate(model)
    return output_dir / model["id"] / Path(model["artifact"])


def print_candidate(model: dict, output_dir: Path) -> None:
    validate_download_candidate(model)
    fully_pinned = (
        model.get("artifact_size_bytes") is not None
        and model.get("artifact_sha256") is not None
    )
    print(
        json.dumps(
            {
                "id": model["id"],
                "purpose": model.get("purpose"),
                "revision": model["revision"],
                "url": artifact_url(model),
                "destination": str(destination_for(model, output_dir)),
                "metadata_status": "verified-lock-metadata" if fully_pinned else "unverified-lock-metadata",
                "expected_size_bytes": model.get("artifact_size_bytes"),
                "expected_sha256": model.get("artifact_sha256"),
            },
            sort_keys=True,
        )
    )


def download_model(model: dict, destination: Path, allow_unverified_metadata: bool) -> None:
    validate_download_candidate(model)
    expected_size = model.get("artifact_size_bytes")
    expected_sha = model.get("artifact_sha256")
    fully_pinned = expected_size is not None and expected_sha is not None

    if not fully_pinned and not allow_unverified_metadata:
        raise StageError(
            f"{model['id']}: artifact size/SHA-256 are not both pinned; "
            "refusing download without --allow-unverified-metadata"
        )

    destination.parent.mkdir(parents=True, exist_ok=True)
    request = urllib.request.Request(
        artifact_url(model),
        headers={"User-Agent": "PhraseLayer-model-stager/1"},
    )

    fd, temp_name = tempfile.mkstemp(prefix=destination.name + ".", suffix=".part", dir=destination.parent)
    os.close(fd)
    temp_path = Path(temp_name)

    try:
        with urllib.request.urlopen(request, timeout=120) as response, temp_path.open("wb") as target:
            shutil.copyfileobj(response, target, length=1024 * 1024)

        size, sha256 = file_metadata(temp_path)
        verified = verify_metadata(model, size, sha256)
        os.replace(temp_path, destination)
        print(
            json.dumps(
                {
                    "id": model["id"],
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


def verify_existing(model: dict, destination: Path, allow_unverified_metadata: bool) -> None:
    if not destination.is_file():
        raise StageError(f"{model['id']}: staged artifact is missing: {destination}")
    size, sha256 = file_metadata(destination)
    verified = verify_metadata(model, size, sha256)
    if not verified and not allow_unverified_metadata:
        raise StageError(
            f"{model['id']}: local file can be measured but lock metadata is incomplete; "
            "use --allow-unverified-metadata to report discovery values"
        )
    print(
        json.dumps(
            {
                "id": model["id"],
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
    parser.add_argument("--list", action="store_true", help="print resolved pinned URLs without network access")
    parser.add_argument("--verify-only", action="store_true", help="verify already-staged files; do not download")
    parser.add_argument(
        "--allow-unverified-metadata",
        action="store_true",
        help="allow models whose size or SHA-256 is not yet locked; prints measured metadata",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    try:
        candidates = load_candidates(args.lock)
        selected = select_candidates(candidates, args.model, args.purpose_prefix)
        for model in selected:
            if args.list:
                print_candidate(model, args.output_dir)
                continue
            destination = destination_for(model, args.output_dir)
            if args.verify_only:
                verify_existing(model, destination, args.allow_unverified_metadata)
            else:
                download_model(model, destination, args.allow_unverified_metadata)
        return 0
    except (OSError, ValueError, StageError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
