#!/usr/bin/env python3
"""Build the deterministic PhraseLayer tokenizer parity fixture manifest from a passed OPUS-MT probe report."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
from pathlib import Path
from typing import Any

MODEL_ID = "Helsinki-NLP/opus-mt-en-jap"
REVISION = "a863894cdd2b80f3bc1c5966734aee9ffec207d1"
MAGIC = "PHRASELAYER_TRANSLATION_TOKENIZER_FIXTURES_V1"
DECODER_START_TOKEN_ID = 46275
EOS_TOKEN_ID = 0
PAD_TOKEN_ID = 46275


class FixtureManifestError(RuntimeError):
    pass


def b64(value: str) -> str:
    return base64.b64encode(value.encode("utf-8")).decode("ascii")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def int_list(value: Any, label: str) -> list[int]:
    if not isinstance(value, list) or not value:
        raise FixtureManifestError(f"{label} must be a non-empty integer list")
    result: list[int] = []
    for item in value:
        if not isinstance(item, int) or item < 0:
            raise FixtureManifestError(f"{label} contains an invalid token id")
        result.append(item)
    return result


def strip_decoder_control_tokens(values: list[int]) -> list[int]:
    result: list[int] = []
    for index, token_id in enumerate(values):
        if index == 0 and token_id == DECODER_START_TOKEN_ID:
            continue
        if token_id == EOS_TOKEN_ID:
            break
        if token_id == PAD_TOKEN_ID:
            continue
        result.append(token_id)
    if not result:
        raise FixtureManifestError("reference translation produced no semantic target tokens")
    return result


def prepare(report_path: Path, output: Path) -> dict[str, Any]:
    report = json.loads(report_path.read_text(encoding="utf-8"))
    if report.get("model_id") != MODEL_ID or report.get("revision") != REVISION:
        raise FixtureManifestError("translation probe identity drift")
    if report.get("status") != "pass":
        raise FixtureManifestError("fixture preparation requires status=pass")
    if not report.get("tokenizer_parity", {}).get("exact"):
        raise FixtureManifestError("fixture preparation requires exact exported tokenizer parity")
    if not report.get("managed_tokenizer_parity", {}).get("exact"):
        raise FixtureManifestError("fixture preparation requires exact managed tokenizer parity")

    tokenizer_reference = report.get("tokenizer_reference")
    if not isinstance(tokenizer_reference, dict):
        raise FixtureManifestError("tokenizer_reference is missing")
    source_samples = tokenizer_reference.get("samples")
    reference_samples = report.get("reference_samples")
    if not isinstance(source_samples, list) or not source_samples:
        raise FixtureManifestError("tokenizer_reference.samples is missing")
    if not isinstance(reference_samples, list) or not reference_samples:
        raise FixtureManifestError("reference_samples is missing")

    encode_rows: list[tuple[str, list[int]]] = []
    for index, item in enumerate(source_samples):
        if not isinstance(item, dict) or not isinstance(item.get("source"), str):
            raise FixtureManifestError(f"source fixture {index} is malformed")
        encode_rows.append((item["source"], int_list(item.get("input_ids"), f"source fixture {index} input_ids")))

    decode_rows: list[tuple[list[int], str]] = []
    for index, item in enumerate(reference_samples):
        if not isinstance(item, dict) or not isinstance(item.get("translation"), str):
            raise FixtureManifestError(f"target fixture {index} is malformed")
        generated = int_list(item.get("token_ids"), f"target fixture {index} token_ids")
        decode_rows.append((strip_decoder_control_tokens(generated), item["translation"]))

    lines = [
        MAGIC,
        "model_id_b64\t" + b64(MODEL_ID),
        "revision\t" + REVISION,
        "encode_fixture_count\t" + str(len(encode_rows)),
        "decode_fixture_count\t" + str(len(decode_rows)),
        "END_HEADER",
    ]
    for source, token_ids in encode_rows:
        lines.append("E\t" + b64(source) + "\t" + ",".join(str(value) for value in token_ids))
    for token_ids, expected in decode_rows:
        lines.append("D\t" + ",".join(str(value) for value in token_ids) + "\t" + b64(expected))
    lines.append("END")

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    result = {
        "format": MAGIC,
        "status": "ephemeral-local-runtime-artifact",
        "size_bytes": output.stat().st_size,
        "sha256": sha256_file(output),
        "encode_fixture_count": len(encode_rows),
        "decode_fixture_count": len(decode_rows),
        "uploaded": False,
    }
    report["managed_tokenizer_fixture_manifest"] = result
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = prepare(args.report, args.output)
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
