#!/usr/bin/env python3
"""Fetch PhraseLayer's pinned Moonshine speech parity fixture without committing audio bytes.

The upstream WAV stays outside this repository. Identity is pinned by repository commit, path,
Git blob SHA-1, and byte size. A SHA-256 is computed after download and written to the local
evidence manifest for downstream ONNX Runtime / Unity parity jobs.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import urllib.request
from typing import Any, Dict

UPSTREAM_REPOSITORY = "moonshine-ai/moonshine-v2"
UPSTREAM_REVISION = "49bc3af5bb0d767d5156fb579fa5f9853b559bf3"
UPSTREAM_PATH = "android/java/androidTest/assets/beckett.wav"
EXPECTED_SIZE_BYTES = 318978
EXPECTED_GIT_BLOB_SHA1 = "603c2b82ae532fd569006ef84f72f23b3aa7e37c"
EXPECTED_SPOKEN_TEXT = "Ever tried, ever failed. No matter. Try again. Fail again. Fail better."


class FixtureError(ValueError):
    pass


def git_blob_sha1(data: bytes) -> str:
    header = b"blob " + str(len(data)).encode("ascii") + b"\0"
    return hashlib.sha1(header + data).hexdigest()


def validate_fixture_bytes(data: bytes) -> Dict[str, Any]:
    if len(data) != EXPECTED_SIZE_BYTES:
        raise FixtureError(
            f"beckett.wav size drift: expected {EXPECTED_SIZE_BYTES} bytes but received {len(data)}"
        )
    actual_blob = git_blob_sha1(data)
    if actual_blob != EXPECTED_GIT_BLOB_SHA1:
        raise FixtureError(
            f"beckett.wav Git blob drift: expected {EXPECTED_GIT_BLOB_SHA1} but received {actual_blob}"
        )
    if not data.startswith(b"RIFF") or data[8:12] != b"WAVE":
        raise FixtureError("beckett.wav is not a RIFF/WAVE file")
    return {
        "size_bytes": len(data),
        "git_blob_sha1": actual_blob,
        "sha256": hashlib.sha256(data).hexdigest(),
    }


def raw_url() -> str:
    return (
        "https://raw.githubusercontent.com/"
        + UPSTREAM_REPOSITORY
        + "/"
        + UPSTREAM_REVISION
        + "/"
        + UPSTREAM_PATH
    )


def fetch(output: pathlib.Path, manifest: pathlib.Path) -> Dict[str, Any]:
    request = urllib.request.Request(raw_url(), headers={"User-Agent": "PhraseLayer-Moonshine-Parity/1"})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            data = response.read()
    except Exception as exc:
        raise FixtureError(f"failed to fetch pinned beckett.wav: {exc}") from exc

    identity = validate_fixture_bytes(data)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(data)
    report = {
        "schema_version": 1,
        "fixture": "beckett.wav",
        "upstream_repository": UPSTREAM_REPOSITORY,
        "upstream_revision": UPSTREAM_REVISION,
        "upstream_path": UPSTREAM_PATH,
        "source_url": raw_url(),
        "spoken_text": EXPECTED_SPOKEN_TEXT,
        **identity,
    }
    manifest.parent.mkdir(parents=True, exist_ok=True)
    manifest.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return report


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--manifest", type=pathlib.Path, required=True)
    args = parser.parse_args()
    print(json.dumps(fetch(args.output, args.manifest), sort_keys=True))


if __name__ == "__main__":
    main()
