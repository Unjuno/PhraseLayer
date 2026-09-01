#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import pathlib
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).with_name("prepare_moonshine_token_decoder_assets.py")
SPEC = importlib.util.spec_from_file_location("prepare_moonshine_token_decoder_assets", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)


def build_tokenizer() -> dict:
    tokens = [f"token-{index}" for index in range(subject.EXPECTED_BASE_COUNT)]
    tokens[0] = "<unk>"
    tokens[1] = "<s>"
    tokens[2] = "</s>"
    tokens[3] = "<0xE3>"
    tokens[4] = "▁hello"
    vocab = {content: index for index, content in enumerate(tokens)}
    added = [
        {"id": 0, "content": "<unk>", "special": True},
        {"id": 1, "content": "<s>", "special": True},
        {"id": 2, "content": "</s>", "special": True},
    ]
    added.extend(
        {"id": 32000 + offset, "content": f"<<ST_{offset}>>", "special": True}
        for offset in range(768)
    )
    return {
        "model": {
            "type": "BPE",
            "vocab": vocab,
            "byte_fallback": True,
        },
        "normalizer": {
            "type": "Sequence",
            "normalizers": [
                {"type": "Prepend", "prepend": "▁"},
                {"type": "Replace", "pattern": {"String": " "}, "content": "▁"},
            ],
        },
        "pre_tokenizer": None,
        "decoder": {
            "type": "Sequence",
            "decoders": [
                {"type": "Replace", "pattern": {"String": "▁"}, "content": " "},
                {"type": "ByteFallback"},
                {"type": "Fuse"},
                {"type": "Strip", "content": " ", "start": 1, "stop": 0},
            ],
        },
        "added_tokens": added,
    }


def parse_binary(data: bytes) -> list[bytes]:
    output = []
    offset = 0
    while offset < len(data):
        first = data[offset]
        offset += 1
        if first == 0:
            output.append(b"")
            continue
        if first < 128:
            length = first
        else:
            second = data[offset]
            offset += 1
            length = second * 128 + first - 128
        output.append(data[offset:offset + length])
        offset += length
    return output


class MoonshineTokenDecoderAssetTests(unittest.TestCase):
    def test_reviewed_contract_builds_id_indexed_native_compatible_asset(self) -> None:
        tokenizer = build_tokenizer()
        entries = subject.build_token_entries(tokenizer)
        self.assertEqual(subject.EXPECTED_TOKEN_COUNT, len(entries))
        self.assertEqual(b"<unk>", entries[0])
        self.assertEqual(bytes([0xE3]), entries[3])
        self.assertEqual("▁hello".encode("utf-8"), entries[4])
        self.assertEqual(b"<<ST_0>>", entries[32000])

        binary = subject.build_binary(entries)
        self.assertEqual(entries, parse_binary(binary))

    def test_prepare_emits_source_and_output_hashes(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            tokenizer = root / "tokenizer.json"
            tokenizer.write_text(json.dumps(build_tokenizer(), ensure_ascii=False), encoding="utf-8")
            output = root / "moonshine_tokens.bin"
            manifest = root / "manifest.json"

            report = subject.prepare(tokenizer, output, manifest)

            self.assertEqual(32768, report["token_count"])
            self.assertEqual(64, len(report["source_tokenizer_sha256"]))
            self.assertEqual(64, len(report["artifact_sha256"]))
            self.assertEqual(output.stat().st_size, report["artifact_size_bytes"])
            self.assertEqual(report, json.loads(manifest.read_text(encoding="utf-8")))

    def test_decoder_or_special_contract_drift_is_rejected(self) -> None:
        tokenizer = build_tokenizer()
        tokenizer["decoder"]["decoders"][0]["content"] = "_"
        with self.assertRaisesRegex(subject.TokenDecoderAssetError, "Replace"):
            subject.build_token_entries(tokenizer)

        tokenizer = build_tokenizer()
        tokenizer["added_tokens"][0]["special"] = False
        with self.assertRaisesRegex(subject.TokenDecoderAssetError, "special"):
            subject.build_token_entries(tokenizer)

    def test_conflicting_added_token_spelling_is_rejected(self) -> None:
        tokenizer = build_tokenizer()
        tokenizer["added_tokens"][1]["content"] = "<different>"
        with self.assertRaisesRegex(subject.TokenDecoderAssetError, "conflicting spelling"):
            subject.build_token_entries(tokenizer)


if __name__ == "__main__":
    unittest.main()
