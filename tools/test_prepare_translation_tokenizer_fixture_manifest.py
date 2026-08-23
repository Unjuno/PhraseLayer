#!/usr/bin/env python3
from __future__ import annotations

import base64
import json
import tempfile
from pathlib import Path

import prepare_translation_tokenizer_fixture_manifest as fixture


def build_report(path: Path) -> None:
    report = {
        "model_id": fixture.MODEL_ID,
        "revision": fixture.REVISION,
        "status": "pass",
        "tokenizer_parity": {"exact": True},
        "managed_tokenizer_parity": {"exact": True},
        "tokenizer_reference": {
            "samples": [
                {"source": "hello", "input_ids": [42, 0]},
                {"source": "keep off", "input_ids": [7, 8, 0]},
            ]
        },
        "reference_samples": [
            {"source": "hello", "token_ids": [46275, 100, 101, 0], "translation": "こんにちは"},
            {"source": "keep off", "token_ids": [46275, 200, 0], "translation": "立入禁止"},
        ],
    }
    path.write_text(json.dumps(report), encoding="utf-8")


def decode(value: str) -> str:
    return base64.b64decode(value).decode("utf-8")


def test_fixture_manifest_is_deterministic_and_control_tokens_are_removed() -> None:
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        report = root / "report.json"
        output = root / "fixtures.txt"
        build_report(report)

        result = fixture.prepare(report, output)
        lines = output.read_text(encoding="utf-8").splitlines()

        assert result["encode_fixture_count"] == 2
        assert result["decode_fixture_count"] == 2
        encode = next(line for line in lines if line.startswith("E\t"))
        fields = encode.split("\t")
        assert decode(fields[1]) == "hello"
        assert fields[2] == "42,0"
        decode_row = next(line for line in lines if line.startswith("D\t"))
        fields = decode_row.split("\t")
        assert fields[1] == "100,101"
        assert decode(fields[2]) == "こんにちは"

        updated = json.loads(report.read_text(encoding="utf-8"))
        assert updated["managed_tokenizer_fixture_manifest"]["uploaded"] is False


def test_non_exact_managed_parity_is_rejected() -> None:
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        report = root / "report.json"
        output = root / "fixtures.txt"
        build_report(report)
        data = json.loads(report.read_text(encoding="utf-8"))
        data["managed_tokenizer_parity"]["exact"] = False
        report.write_text(json.dumps(data), encoding="utf-8")

        try:
            fixture.prepare(report, output)
        except fixture.FixtureManifestError as error:
            assert "managed tokenizer parity" in str(error)
        else:
            raise AssertionError("non-exact managed tokenizer parity must fail closed")


def main() -> int:
    test_fixture_manifest_is_deterministic_and_control_tokens_are_removed()
    test_non_exact_managed_parity_is_rejected()
    print("PASS: tokenizer fixture manifest is deterministic, parity-gated, and strips decoder control tokens")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
