#!/usr/bin/env python3
"""Stage revision-pinned Marian tokenizer assets into Unity Resources without model weights."""

import argparse
import hashlib
import json
import re
import shutil
from pathlib import Path

HEX40 = re.compile(r"^[0-9a-f]{40}$")
EXPECTED_VOCAB_SIZE = 46276
ASSETS = (
    ("source.spm", "source.spm.bytes"),
    ("target.spm", "target.spm.bytes"),
    ("vocab.json", "vocab.json"),
)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def stage(snapshot_dir: Path, revision: str, destination: Path, manifest_path: Path) -> dict:
    if not HEX40.fullmatch(revision):
        raise ValueError("revision must be a full lowercase 40-character Git SHA")
    if not snapshot_dir.is_dir():
        raise ValueError(f"snapshot directory does not exist: {snapshot_dir}")

    vocab_path = snapshot_dir / "vocab.json"
    try:
        vocabulary = json.loads(vocab_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError(f"invalid vocab.json: {error}") from error

    if not isinstance(vocabulary, dict) or len(vocabulary) != EXPECTED_VOCAB_SIZE:
        raise ValueError(
            f"vocab.json must contain exactly {EXPECTED_VOCAB_SIZE} entries"
        )
    ids = list(vocabulary.values())
    if (
        any(not isinstance(token_id, int) for token_id in ids)
        or len(set(ids)) != EXPECTED_VOCAB_SIZE
        or set(ids) != set(range(EXPECTED_VOCAB_SIZE))
    ):
        raise ValueError("vocab.json ids must uniquely cover 0..46275")

    destination.mkdir(parents=True, exist_ok=True)
    artifacts = []
    for source_name, target_name in ASSETS:
        source = snapshot_dir / source_name
        if not source.is_file() or source.stat().st_size <= 0:
            raise ValueError(f"missing or empty tokenizer asset: {source}")
        target = destination / target_name
        shutil.copy2(source, target)
        artifacts.append(
            {
                "source": source_name,
                "file": target_name,
                "size_bytes": target.stat().st_size,
                "sha256": sha256(target),
            }
        )

    manifest = {
        "schema_version": 1,
        "model_id": "Helsinki-NLP/opus-mt-en-jap",
        "revision": revision,
        "resource_root": "LocalTranslationAssets",
        "artifacts": artifacts,
        "weights_staged": False,
        "runtime_compatibility": "unverified-real-unity-import-and-quest-execution-required",
    }
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(
        json.dumps(manifest, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--snapshot-dir", type=Path, required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument(
        "--destination",
        type=Path,
        default=Path(
            "unity/PhraseLayer.Unity/Assets/Resources/LocalTranslationAssets"
        ),
    )
    parser.add_argument(
        "--manifest",
        type=Path,
        default=Path(
            "artifacts/translation/unity-marian-tokenizer-assets.manifest.json"
        ),
    )
    args = parser.parse_args()

    manifest = stage(args.snapshot_dir, args.revision, args.destination, args.manifest)
    print(
        "PASS: staged "
        f"{len(manifest['artifacts'])} Marian tokenizer assets; manifest={args.manifest}"
    )


if __name__ == "__main__":
    main()
