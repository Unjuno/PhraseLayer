#!/usr/bin/env python3
"""Extract the PP-OCR CTC dictionary from revision-matched Paddle inference YAML.

The pinned PP-OCRv6 tiny recognizer stores `PostProcess.character_dict` in
`inference.yml`. This script parses only the reviewed YAML subset instead of
introducing a general YAML dependency. It writes raw dictionary tokens exactly
one per UTF-8 line; Paddle's separate `use_space_char` contract remains separate
and is recorded in the generated manifest.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_LOCK = ROOT / "models" / "models.lock.json"
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
EXPECTED_SOURCE_FORMAT = "paddle-inference-yaml"
EXPECTED_YAML_PATH = ["PostProcess", "character_dict"]


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


def _require_positive_int(contract: dict, key: str, model_id: str) -> int:
    value = contract.get(key)
    if not isinstance(value, int) or value <= 0:
        raise DictionaryExportError(f"{model_id}: {key} must be a positive integer")
    return value


def _require_sha256(contract: dict, key: str, model_id: str) -> str:
    value = contract.get(key)
    if not isinstance(value, str) or SHA256_RE.fullmatch(value) is None:
        raise DictionaryExportError(
            f"{model_id}: {key} must be 64 lowercase hexadecimal characters"
        )
    return value


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
    source_format = contract.get("source_format")
    yaml_path = contract.get("yaml_path")
    use_space_char = contract.get("use_space_char")

    if not isinstance(source_artifact, str) or not source_artifact:
        raise DictionaryExportError(f"{model_id}: source_artifact is missing")
    if source_format != EXPECTED_SOURCE_FORMAT:
        raise DictionaryExportError(
            f"{model_id}: source_format must be {EXPECTED_SOURCE_FORMAT!r}"
        )
    if yaml_path != EXPECTED_YAML_PATH:
        raise DictionaryExportError(
            f"{model_id}: yaml_path must remain {EXPECTED_YAML_PATH!r}"
        )
    if not isinstance(generated_artifact, str) or not generated_artifact:
        raise DictionaryExportError(f"{model_id}: generated_artifact is missing")
    if not isinstance(generated_manifest, str) or not generated_manifest:
        raise DictionaryExportError(f"{model_id}: generated_manifest is missing")
    if not isinstance(postprocess_name, str) or not postprocess_name:
        raise DictionaryExportError(f"{model_id}: postprocess_name is missing")
    if not isinstance(use_space_char, bool):
        raise DictionaryExportError(f"{model_id}: use_space_char must be boolean")

    _require_positive_int(contract, "raw_token_count", model_id)
    _require_positive_int(contract, "effective_token_count", model_id)
    _require_positive_int(contract, "generated_artifact_size_bytes", model_id)
    _require_sha256(contract, "generated_artifact_sha256", model_id)

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
    source = matches[0]
    if source.get("artifact_size_bytes") is None or source.get("artifact_sha256") is None:
        raise DictionaryExportError(
            f"{model_id}: dictionary source artifact must have pinned size and SHA-256"
        )

    return contract


def decode_yaml_scalar(raw: str) -> str:
    if raw.startswith("'"):
        if len(raw) < 2 or not raw.endswith("'"):
            raise DictionaryExportError(f"unterminated single-quoted YAML scalar: {raw!r}")
        return raw[1:-1].replace("''", "'")

    if raw.startswith('"'):
        if len(raw) < 2 or not raw.endswith('"'):
            raise DictionaryExportError(f"unterminated double-quoted YAML scalar: {raw!r}")
        try:
            value = json.loads(raw)
        except json.JSONDecodeError as error:
            raise DictionaryExportError(
                f"unsupported double-quoted YAML scalar: {raw!r}"
            ) from error
        if not isinstance(value, str):
            raise DictionaryExportError(f"YAML scalar is not a string: {raw!r}")
        return value

    return raw


def _postprocess_block(lines: list[str]) -> list[str]:
    indices = [index for index, line in enumerate(lines) if line == "PostProcess:"]
    if len(indices) != 1:
        raise DictionaryExportError(
            f"expected exactly one top-level PostProcess block; found {len(indices)}"
        )

    start = indices[0] + 1
    end = len(lines)
    for index in range(start, len(lines)):
        line = lines[index]
        if line and not line[0].isspace() and not line.lstrip().startswith("#"):
            end = index
            break
    return lines[start:end]


def extract_tokens(metadata_text: str, contract: dict) -> list[str]:
    if not isinstance(metadata_text, str):
        raise DictionaryExportError("export metadata text must be a string")

    block = _postprocess_block(metadata_text.splitlines())

    name_lines = [line for line in block if line.startswith("  name:")]
    if len(name_lines) != 1:
        raise DictionaryExportError(
            f"expected exactly one PostProcess.name entry; found {len(name_lines)}"
        )
    actual_postprocess = decode_yaml_scalar(name_lines[0].split(":", 1)[1].lstrip())
    expected_postprocess = contract["postprocess_name"]
    if actual_postprocess != expected_postprocess:
        raise DictionaryExportError(
            f"postprocess mismatch: expected {expected_postprocess!r}, got {actual_postprocess!r}"
        )

    dictionary_indices = [
        index for index, line in enumerate(block) if line == "  character_dict:"
    ]
    if len(dictionary_indices) != 1:
        raise DictionaryExportError(
            f"expected exactly one PostProcess.character_dict entry; found {len(dictionary_indices)}"
        )

    tokens: list[str] = []
    start = dictionary_indices[0] + 1
    for line in block[start:]:
        if not line.startswith("  - "):
            break
        token = decode_yaml_scalar(line[4:])
        if "\n" in token or "\r" in token:
            raise DictionaryExportError(
                f"character_dict[{len(tokens)}] contains a newline and cannot be represented in Paddle's line dictionary format"
            )
        tokens.append(token)

    if not tokens:
        raise DictionaryExportError("character_dict must be a non-empty YAML list")

    if contract["use_space_char"] and " " in tokens:
        raise DictionaryExportError(
            "character_dict already contains a literal single-space token while use_space_char=true; "
            "PaddleOCR would append another space token, so refuse ambiguous export metadata"
        )

    return tokens


def dictionary_bytes(tokens: list[str]) -> bytes:
    return ("".join(token + "\n" for token in tokens)).encode("utf-8")


def verify_generated_contract(contract: dict, tokens: list[str], output_bytes: bytes) -> str:
    raw_count = len(tokens)
    effective_count = raw_count + (1 if contract["use_space_char"] else 0)
    digest = hashlib.sha256(output_bytes).hexdigest()

    if raw_count != contract["raw_token_count"]:
        raise DictionaryExportError(
            f"raw token count mismatch: expected {contract['raw_token_count']}, got {raw_count}"
        )
    if effective_count != contract["effective_token_count"]:
        raise DictionaryExportError(
            "effective token count mismatch: expected "
            f"{contract['effective_token_count']}, got {effective_count}"
        )
    if len(output_bytes) != contract["generated_artifact_size_bytes"]:
        raise DictionaryExportError(
            "generated dictionary size mismatch: expected "
            f"{contract['generated_artifact_size_bytes']}, got {len(output_bytes)}"
        )
    if digest != contract["generated_artifact_sha256"]:
        raise DictionaryExportError(
            "generated dictionary SHA-256 mismatch: expected "
            f"{contract['generated_artifact_sha256']}, got {digest}"
        )
    return digest


def build_manifest(
    model: dict,
    contract: dict,
    tokens: list[str],
    output_bytes: bytes,
    digest: str,
) -> dict:
    use_space_char = contract["use_space_char"]
    return {
        "schema_version": 1,
        "model_id": model["id"],
        "upstream": model["upstream"],
        "revision": model["revision"],
        "source_artifact": contract["source_artifact"],
        "source_format": contract["source_format"],
        "postprocess_name": contract["postprocess_name"],
        "raw_token_count": len(tokens),
        "raw_contains_literal_space": " " in tokens,
        "use_space_char": use_space_char,
        "effective_token_count": len(tokens) + (1 if use_space_char else 0),
        "generated_artifact": contract["generated_artifact"],
        "generated_size_bytes": len(output_bytes),
        "generated_sha256": digest,
    }


def export_dictionary(
    lock_path: Path,
    model_id: str,
    metadata_path: Path,
    output_dir: Path,
) -> tuple[Path, Path, dict]:
    model = load_model(lock_path, model_id)
    contract = validate_dictionary_contract(model)
    metadata_text = metadata_path.read_text(encoding="utf-8")

    tokens = extract_tokens(metadata_text, contract)
    output_bytes = dictionary_bytes(tokens)
    digest = verify_generated_contract(contract, tokens, output_bytes)
    manifest = build_manifest(model, contract, tokens, output_bytes, digest)

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
