#!/usr/bin/env python3
"""Resolve Moonshine Tiny source-weight identity without downloading model weights.

This helper performs metadata/HEAD requests only. It is intentionally separate from the
small-snapshot staging helper so normal Core CI can remain deterministic and weight-free.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import re
from typing import Any, Callable, Dict, Optional

MODEL_ID = "moonshine-ai/moonshine-tiny"
PINNED_REVISION = "390624ed33d594443aa4aa221f5b9f283b545b5a"
WEIGHT_FILENAME = "model.safetensors"
FULL_REVISION_RE = re.compile(r"^[0-9a-f]{40}$")
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")


class WeightMetadataError(RuntimeError):
    pass


def _normalize_etag(value: Any) -> str:
    if not isinstance(value, str):
        raise WeightMetadataError("Moonshine weight metadata did not expose a string ETag")
    candidate = value.strip()
    if candidate.startswith("W/"):
        candidate = candidate[2:].strip()
    if len(candidate) >= 2 and candidate[0] == candidate[-1] == '"':
        candidate = candidate[1:-1]
    candidate = candidate.lower()
    if SHA256_RE.fullmatch(candidate) is None:
        raise WeightMetadataError(
            "Moonshine weight ETag is not a lowercase 64-character content SHA256: " + candidate
        )
    return candidate


def fetch_weight_metadata(
    revision: str = PINNED_REVISION,
    *,
    build_url: Optional[Callable[..., str]] = None,
    get_metadata: Optional[Callable[[str], Any]] = None,
) -> Dict[str, Any]:
    if FULL_REVISION_RE.fullmatch(revision or "") is None:
        raise WeightMetadataError("Moonshine revision must be a full lowercase 40-character SHA")
    if revision != PINNED_REVISION:
        raise WeightMetadataError(
            f"Moonshine revision drift: expected {PINNED_REVISION}, received {revision}"
        )

    if build_url is None or get_metadata is None:
        try:
            from huggingface_hub import get_hf_file_metadata, hf_hub_url
        except ImportError as exc:
            raise WeightMetadataError("huggingface_hub is required for online Moonshine weight probing") from exc
        if build_url is None:
            build_url = hf_hub_url
        if get_metadata is None:
            get_metadata = get_hf_file_metadata

    url = build_url(repo_id=MODEL_ID, filename=WEIGHT_FILENAME, revision=revision)
    metadata = get_metadata(url)

    commit_hash = getattr(metadata, "commit_hash", None)
    if not isinstance(commit_hash, str) or FULL_REVISION_RE.fullmatch(commit_hash) is None:
        raise WeightMetadataError("Moonshine weight metadata did not resolve a full commit SHA")
    if commit_hash != PINNED_REVISION:
        raise WeightMetadataError(
            f"Moonshine weight commit drift: expected {PINNED_REVISION}, resolved {commit_hash}"
        )

    size = getattr(metadata, "size", None)
    if not isinstance(size, int) or isinstance(size, bool) or size <= 0:
        raise WeightMetadataError("Moonshine weight metadata did not expose a positive byte size")

    sha256 = _normalize_etag(getattr(metadata, "etag", None))
    return {
        "schema_version": 1,
        "model_id": MODEL_ID,
        "revision": PINNED_REVISION,
        "artifact": {
            "name": WEIGHT_FILENAME,
            "size_bytes": size,
            "sha256": sha256,
        },
        "identity_source": "huggingface-head-metadata-etag",
        "local_file_hash_required_before_export": True,
        "weight_downloaded": False,
        "bundled": False,
    }


def write_manifest(path: pathlib.Path, manifest: Dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--revision", default=PINNED_REVISION)
    parser.add_argument("--output", type=pathlib.Path)
    args = parser.parse_args()

    manifest = fetch_weight_metadata(args.revision)
    if args.output is not None:
        write_manifest(args.output, manifest)
    print(json.dumps(manifest, sort_keys=True))


if __name__ == "__main__":
    main()
