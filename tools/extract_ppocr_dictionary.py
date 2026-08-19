#!/usr/bin/env python3
"""Extract a PaddleOCR CTC character dictionary from revision-matched export metadata.

This script intentionally writes the raw PostProcess.character_dict list exactly
as PaddleOCR's inference path does. The separate use_space_char flag remains a
separate postprocess contract and is recorded in the generated manifest.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_LOCK = ROOT / "models" / "models.lock.json"


class DictionaryExportError(RuntimeError):
    pass


def load_model(lock_path: Path, model_id: str) -> dict:
    payload = json.loads(lock_path.read_text(encoding="utf-8"))
    if payload.get("schema_version") != 2:
        raise DictionaryExportError(
            f"unsupported lock schema: {payload.get('schema_version')!r}"
        )
    candidates = payload.get("candidates")
    if not isinstance(candidates, list):
        raise DictionaryExportError("models.lock.json candidates must be a list")

    matches = [model for model in candidates if model.get("id") == model_id]
    if len(matches) != 1:
        raise DictionaryExportError(
            f"expected exactly one model with id {model_id!r}; found {len(matches)}"
        )
    return matches[0]


def validate_dictionary_contract(model: dict) -> dict:
    model_id = model.get("id", "<unknown>")
    contract = model.get("recognition_dictionary")
    if not isinstance(contract, dict):
        raise DictionaryExportError(
            f"{model_id}: recognition_dictionary contract is missing"
        )

    source_artifact = contract.get("source_artifact")
    generated_artifact = contract.get("generated_artifact")
    generated_manifest = contract.get("generated_manifest")
    postprocess_name = contract.get("postprocess_name")
    json_path = contract.get("json_path")
    use_space_char = contract.get("use_space_char")

    if not isinstance(source_artifact, str) or not source_artifact:
        raise DictionaryExportError(f"{model_id}: source_artifact is missing")
    if not isinstance(generated_artifact, str) or not generated_artifact:
        raise DictionaryExportError(f"{model_id}: generated_artifact is missing")
    if not isinstance(generated_manifest, str) or not generated_manifest:
        raise DictionaryExportError(f"{model_id}: generated_manifest is missing")
    if not isinstance(postprocess_name, str) or not postprocess_name:
        raise DictionaryExportError(f"{model_id}: postprocess_name is missing")
    if not isinstance(json_path, list) or not json_path or not all(
        isinstance(segment, str) and segment for segment in json_path
    ):
        raise DictionaryExportError(
            f"{model_id}: json_path must be a non-empty list of property names"
        )
    if not isinstance(use_space_char, bool):
        raise DictionaryExportError(f"{model_id}: use_space_char must be boolean")

    support_artifacts = model.get("support_artifacts", [])
    if not isinstance(support_artifacts, list):
        raise DictionaryExportError(f"{model_id}: support_artifacts must be a list")
    matches = [
        artifact
        for artifact in support_artifacts
        if isinstance(artifact, dict) and artifact.get("artifact") == source_artifact
    ]
    if len(matches) != 1:
        raise DictionaryExportError(
            f"{model_id}: source_artifact {source_artifact!r} must appear exactly once in support_artifacts"
        )

    return contract


def value_at_path(payload: object, path: list[str]) -> object:
    current = payload
    for segment in path:
        if not isinstance(current, dict) or segment not in current:
            raise DictionaryExportError(
                "export metadata is missing JSON path: " + ".".join(path)
            )
        current = current[segment]
    return current


def extract_tokens(metadata: dict, contract: dict) -> list[str]:
    postprocess = metadata.get("PostProcess")
    if not isinstance(postprocess, dict):
        raise DictionaryExportError("export metadata PostProcess object is missing")

    expected_postprocess = contract["postprocess_name"]
    actual_postprocess = postprocess.get("name")
    if actual_postprocess != expected_postprocess:
        raise DictionaryExportError(
            f"postprocess mismatch: expected {expected_postprocess!r}, got {actual_postprocess!r}"
        )

    raw_tokens = value_at_path(metadata, contract["json_path"])
    if not isinstance(raw_tokens, list) or not raw_tokens:
        raise DictionaryExportError("character_dict must be a non-empty list")

    tokens: list[str] = []
    for index, token in enumerate(raw_tokens):
        if not isinstance(token, str):
            raise DictionaryExportError(
                f"character_dict[{index}] must be a string, got {type(token).__name__}"
            )
        if "\n" in token or "\r" in token:
            raise DictionaryExportError(
                f"character_dict[{index}] contains a newline and cannot be represented in Paddle's line dictionary format"
            )
        tokens.append(token)

    if contract["use_space_char"] and " " in tokens:
        raise DictionaryExportError(
            "character_dict already contains a literal single-space token while use_space_char=true; "
            "PaddleOCR would append another space token, so refuse ambiguous export metadata"
        )

    return tokens


def dictionary_bytes(tokens: list[str]) -> bytes:
    return ("".join(token + "\n" for token in tokens)).encode("utf-8")


def build_manifest(model: dict, contract: dict, tokens: list[str], output_bytes: bytes) -> dict:
    use_space_char = contract["use_space_char"]
    return {
        "schema_version": 1,
        "model_id": model["id"],
        "upstream": model["upstream"],
        "revision": model["revision"],
        "source_artifact": contract["source_artifact"],
        "postprocess_name": contract["postprocess_name"],
        "raw_token_count": len(tokens),
        "raw_contains_literal_space": " " in tokens,
        "use_space_char": use_space_char,
        "effective_token_count": len(tokens) + (1 if use_space_char else 0),
        "generated_artifact": contract["generated_artifact"],
        "generated_sha256": hashlib.sha256(output_bytes).hexdigest(),
    }


def export_dictionary(
    lock_path: Path,
    model_id: str,
    metadata_path: Path,
    output_dir: Path,
) -> tuple[Path, Path, dict]:
    model = load_model(lock_path, model_id)
    contract = validate_dictionary_contract(model)
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    if not isinstance(metadata, dict):
        raise DictionaryExportError("export metadata root must be a JSON object")

    tokens = extract_tokens(metadata, contract)
    output_bytes = dictionary_bytes(tokens)
    manifest = build_manifest(model, contract, tokens, output_bytes)

    dictionary_path = output_dir / contract["generated_artifact"]
    manifest_path = output_dir / contract["generated_manifest"]
    dictionary_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    dictionary_path.write_bytes(output_bytes)
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    return dictionary_path, manifest_path, manifest


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", type=Path, default=DEFAULT_LOCK)
    parser.add_argument("--model", default="pp-ocrv6-tiny-rec")
    parser.add_argument("--metadata", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    try:
        dictionary_path, manifest_path, manifest = export_dictionary(
            args.lock,
            args.model,
            args.metadata,
            args.output_dir,
        )
        print(
            json.dumps(
                {
                    "dictionary": str(dictionary_path),
                    "manifest": str(manifest_path),
                    **manifest,
                },
                ensure_ascii=False,
                sort_keys=True,
            )
        )
        return 0
    except (OSError, ValueError, DictionaryExportError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
