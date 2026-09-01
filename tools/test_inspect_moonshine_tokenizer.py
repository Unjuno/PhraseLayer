#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import pathlib
import unittest

SCRIPT = pathlib.Path(__file__).with_name("inspect_moonshine_tokenizer.py")
SPEC = importlib.util.spec_from_file_location("inspect_moonshine_tokenizer", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)


class MoonshineTokenizerInspectionTests(unittest.TestCase):
    def test_bpe_pipeline_and_added_tokens_are_summarized(self) -> None:
        tokenizer = {
            "model": {
                "type": "BPE",
                "vocab": {"a": 0, "b": 1},
                "unk_token": "<unk>",
                "byte_fallback": False,
            },
            "normalizer": None,
            "pre_tokenizer": {"type": "ByteLevel", "add_prefix_space": False, "trim_offsets": True, "use_regex": True},
            "post_processor": {"type": "ByteLevel", "add_prefix_space": False, "trim_offsets": False, "use_regex": True},
            "decoder": {"type": "ByteLevel", "add_prefix_space": True, "trim_offsets": True, "use_regex": True},
            "added_tokens": [
                {"id": 2, "content": "<eos>", "special": True},
                {"id": 3, "content": "<extra>", "special": False},
            ],
        }

        report = subject.inspect_tokenizer(tokenizer)

        self.assertEqual("BPE", report["model"]["type"])
        self.assertEqual(2, report["model"]["base_vocabulary_size"])
        self.assertEqual("ByteLevel", report["pre_tokenizer"]["type"])
        self.assertEqual("ByteLevel", report["decoder"]["type"])
        self.assertEqual(2, report["added_tokens"]["count"])
        self.assertEqual(1, report["added_tokens"]["special_count"])
        self.assertEqual("<eos>", report["added_tokens"]["interesting_ids"]["2"])

    def test_sequence_components_are_recursive(self) -> None:
        tokenizer = {
            "model": {"type": "WordPiece", "vocab": ["a"]},
            "normalizer": {"type": "Sequence", "normalizers": [{"type": "NFC"}, {"type": "Lowercase"}]},
            "pre_tokenizer": {"type": "Sequence", "pretokenizers": [{"type": "Whitespace"}, {"type": "Digits"}]},
            "post_processor": None,
            "decoder": None,
            "added_tokens": [],
        }

        report = subject.inspect_tokenizer(tokenizer)

        self.assertEqual(["NFC", "Lowercase"], [child["type"] for child in report["normalizer"]["children"]])
        self.assertEqual(["Whitespace", "Digits"], [child["type"] for child in report["pre_tokenizer"]["children"]])

    def test_missing_model_or_bad_added_token_is_rejected(self) -> None:
        with self.assertRaisesRegex(subject.TokenizerInspectionError, "model"):
            subject.inspect_tokenizer({})
        with self.assertRaisesRegex(subject.TokenizerInspectionError, "content"):
            subject.inspect_tokenizer({
                "model": {"type": "BPE", "vocab": {"a": 0}},
                "normalizer": None,
                "pre_tokenizer": None,
                "post_processor": None,
                "decoder": None,
                "added_tokens": [{"id": 1}],
            })


if __name__ == "__main__":
    unittest.main()
