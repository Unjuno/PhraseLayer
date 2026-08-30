#!/usr/bin/env python3
"""Stage only reviewed Moonshine Tiny metadata/tokenizer files from the pinned Hub revision.

Normal PhraseLayer CI does not need network access to run the contract fixtures. This helper is for a
review/probe environment with huggingface_hub installed. It never requests model.safetensors or ONNX files.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import pathlib
import re
from typing import Any, Callable, Dict, Optional

MODEL_ID = "moonshine-ai/moonshine-tiny"
PINNED_REVISION = "390624ed33d594443aa4aa221f5b9f283b545b5a"
FULL_REVISION_RE = re.compile(r"^[0-9a-f]{40}$")
SMALL_ARTIFACTS = (
    "README.md",
    "config.json",
    "generation_config.json",
    "preprocessor_config.json",
    "tokenizer.json",
)


class SnapshotFetchError(RuntimeError):
    pass


def _load_contract_module():
    path = pathlib.Path(__file__).with_name("validate_moonshine_snapshot_contract.py")
    spec = importlib.util.spec_from_file_location("validate_moonshine_snapshot_contract", path)
    if spec is None or spec.loader is None:
        raise SnapshotFetchError("failed to load Moonshine snapshot contract validator")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def resolve_revision(
    requested_revision: str = PINNED_REVISION,
    *,
    model_info: Optional[Callable[..., Any]] = None,
) -> str:
    if not isinstance(requested_revision, str) or not requested_revision:
        raise SnapshotFetchError("requested Moonshine revision must not be empty")
    if model_info is None:
        try:
            from huggingface_hub import model_info as hub_model_info
        except ImportError as exc:
            raise SnapshotFetchError("huggingface_hub is required for online Moonshine staging") from exc
        model_info = hub_model_info

    info = model_info(MODEL_ID, revision=requested_revision)
    full_revision = getattr(info, "sha", None)
    if not isinstance(full_revision, str) or FULL_REVISION_RE.fullmatch(full_revision) is None:
        raise SnapshotFetchError("Hugging Face did not resolve Moonshine to a full lowercase 40-character SHA")
    if full_revision != PINNED_REVISION:
        raise SnapshotFetchError(
            f"Moonshine revision drift: expected {PINNED_REVISION}, resolved {full_revision}"
        )
    return full_revision


def stage_small_snapshot(
    destination: pathlib.Path,
    full_revision: str,
    *,
    download_file: Optional[Callable[..., str]] = None,
) -> Dict[str, Any]:
    if full_revision != PINNED_REVISION:
        raise SnapshotFetchError("only the reviewed pinned Moonshine revision may be staged")
    destination.mkdir(parents=True, exist_ok=True)

    if download_file is None:
        try:
            from huggingface_hub import hf_hub_download
        except ImportError as exc:
            raise SnapshotFetchError("huggingface_hub is required for online Moonshine staging") from exc
        download_file = hf_hub_download

    for filename in SMALL_ARTIFACTS:
        resolved_path = pathlib.Path(download_file(
            repo_id=MODEL_ID,
            filename=filename,
            revision=full_revision,
            local_dir=str(destination),
        ))
        target = destination / filename
        if not target.is_file():
            if not resolved_path.is_file():
                raise SnapshotFetchError(f"download did not produce expected artifact: {filename}")
            target.write_bytes(resolved_path.read_bytes())
        if target.stat().st_size <= 0:
            raise SnapshotFetchError(f"staged artifact is empty: {filename}")

    prohibited = (
        "model.safetensors",
        "pytorch_model.bin",
        "preprocess.onnx",
        "encode.onnx",
        "uncached_decode.onnx",
        "cached_decode.onnx",
    )
    accidentally_present = sorted(name for name in prohibited if (destination / name).exists())
    if accidentally_present:
        raise SnapshotFetchError(
            "Moonshine metadata destination contains weights/graphs that this tool must not stage: "
            + ", ".join(accidentally_present)
        )

    contract = _load_contract_module()
    manifest = contract.validate_snapshot(destination, full_revision)
    manifest["staging"] = {
        "mode": "huggingface-small-artifacts-only",
        "allow_list": list(SMALL_ARTIFACTS),
        "weights_downloaded": False,
    }
    return manifest


def write_manifest(path: pathlib.Path, manifest: Dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--revision", default=PINNED_REVISION)
    parser.add_argument("--resolve-only", action="store_true")
    parser.add_argument("--destination", type=pathlib.Path)
    parser.add_argument("--output-manifest", type=pathlib.Path)
    args = parser.parse_args()

    full_revision = resolve_revision(args.revision)
    if args.resolve_only:
        print(json.dumps({"model_id": MODEL_ID, "revision": full_revision}, sort_keys=True))
        return
    if args.destination is None or args.output_manifest is None:
        parser.error("--destination and --output-manifest are required unless --resolve-only is used")

    manifest = stage_small_snapshot(args.destination, full_revision)
    write_manifest(args.output_manifest, manifest)
    print(json.dumps({
        "model_id": MODEL_ID,
        "revision": full_revision,
        "artifact_count": len(manifest["artifacts"]),
        "weights_downloaded": False,
    }, sort_keys=True))


if __name__ == "__main__":
    main()
