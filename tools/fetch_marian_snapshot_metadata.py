#!/usr/bin/env python3
"""Resolve and stage only the small reviewed metadata/tokenizer files for OPUS-MT en->ja.

This tool intentionally never downloads model weights. It resolves a Hub revision to a full 40-character commit
SHA, downloads an explicit seven-file allow-list (model card + config/tokenizer artifacts), and delegates contract
validation/fingerprinting to validate_marian_snapshot_contract.py. A discovery ref such as `main` or the currently
observed short SHA may be used, but the emitted evidence always records the resolved full SHA.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import pathlib
import re
from typing import Any, Callable, Dict, Optional

MODEL_ID = "Helsinki-NLP/opus-mt-en-jap"
FULL_REVISION_RE = re.compile(r"^[0-9a-f]{40}$")
SMALL_ARTIFACTS = (
    "README.md",
    "config.json",
    "generation_config.json",
    "tokenizer_config.json",
    "source.spm",
    "target.spm",
    "vocab.json",
)


class SnapshotFetchError(RuntimeError):
    pass


def _load_contract_module():
    path = pathlib.Path(__file__).with_name("validate_marian_snapshot_contract.py")
    spec = importlib.util.spec_from_file_location("validate_marian_snapshot_contract", path)
    if spec is None or spec.loader is None:
        raise SnapshotFetchError("failed to load Marian snapshot contract validator")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _require_full_revision(revision: Any) -> str:
    if not isinstance(revision, str) or FULL_REVISION_RE.fullmatch(revision) is None:
        raise SnapshotFetchError(
            "Hugging Face did not resolve the model ref to a full lowercase 40-character commit SHA"
        )
    return revision


def resolve_revision(
    requested_revision: str,
    *,
    expected_prefix: Optional[str] = None,
    model_info: Optional[Callable[..., Any]] = None,
) -> str:
    if not requested_revision or requested_revision.isspace():
        raise SnapshotFetchError("requested revision must not be empty")
    if expected_prefix is not None:
        if not re.fullmatch(r"[0-9a-f]{7,40}", expected_prefix):
            raise SnapshotFetchError("expected revision prefix must be 7..40 lowercase hexadecimal characters")

    if model_info is None:
        try:
            from huggingface_hub import model_info as hub_model_info
        except ImportError as exc:
            raise SnapshotFetchError(
                "huggingface_hub is required for online revision resolution; install it in the staging environment"
            ) from exc
        model_info = hub_model_info

    info = model_info(MODEL_ID, revision=requested_revision)
    full_revision = _require_full_revision(getattr(info, "sha", None))
    if expected_prefix is not None and not full_revision.startswith(expected_prefix):
        raise SnapshotFetchError(
            f"resolved revision {full_revision} does not start with expected prefix {expected_prefix}"
        )
    return full_revision


def stage_small_snapshot(
    destination: pathlib.Path,
    full_revision: str,
    *,
    download_file: Optional[Callable[..., str]] = None,
) -> Dict[str, Any]:
    _require_full_revision(full_revision)
    destination.mkdir(parents=True, exist_ok=True)

    if download_file is None:
        try:
            from huggingface_hub import hf_hub_download
        except ImportError as exc:
            raise SnapshotFetchError(
                "huggingface_hub is required for online snapshot staging; install it in the staging environment"
            ) from exc
        download_file = hf_hub_download

    for filename in SMALL_ARTIFACTS:
        resolved_path = pathlib.Path(
            download_file(
                repo_id=MODEL_ID,
                filename=filename,
                revision=full_revision,
                local_dir=str(destination),
            )
        )
        target = destination / filename
        if not target.is_file():
            # Some test/download adapters may return a cache path instead of materializing local_dir. Copy only the
            # explicitly reviewed file into the destination; never copy neighboring model files.
            if not resolved_path.is_file():
                raise SnapshotFetchError(f"download did not produce expected artifact: {filename}")
            target.write_bytes(resolved_path.read_bytes())
        if target.stat().st_size <= 0:
            raise SnapshotFetchError(f"staged artifact is empty: {filename}")

    unexpected_weight_names = {
        "pytorch_model.bin",
        "tf_model.h5",
        "model.safetensors",
        "encoder_model.onnx",
        "decoder_model.onnx",
        "decoder_with_past_model.onnx",
    }
    accidentally_present = sorted(name for name in unexpected_weight_names if (destination / name).exists())
    if accidentally_present:
        raise SnapshotFetchError(
            "small-snapshot destination contains model weights/graphs that this tool must not stage: "
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
    path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--revision", default="main")
    parser.add_argument("--expected-prefix")
    parser.add_argument("--resolve-only", action="store_true")
    parser.add_argument("--destination", type=pathlib.Path)
    parser.add_argument("--output-manifest", type=pathlib.Path)
    args = parser.parse_args()

    full_revision = resolve_revision(
        args.revision,
        expected_prefix=args.expected_prefix,
    )
    if args.resolve_only:
        print(json.dumps({"model_id": MODEL_ID, "revision": full_revision}, sort_keys=True))
        return

    if args.destination is None or args.output_manifest is None:
        parser.error("--destination and --output-manifest are required unless --resolve-only is used")

    manifest = stage_small_snapshot(args.destination, full_revision)
    write_manifest(args.output_manifest, manifest)
    print(
        json.dumps(
            {
                "model_id": MODEL_ID,
                "revision": full_revision,
                "artifact_count": len(manifest["artifacts"]),
                "license": manifest["license"],
                "weights_downloaded": False,
            },
            sort_keys=True,
        )
    )


if __name__ == "__main__":
    main()
