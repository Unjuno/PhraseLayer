#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import pathlib
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).with_name("run_moonshine_v1_onnx_reference.py")
SPEC = importlib.util.spec_from_file_location("run_moonshine_v1_onnx_reference", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)


class Meta:
    def __init__(self, name: str) -> None:
        self.name = name


class Session:
    def __init__(self, inputs: int, outputs: int, prefix: str) -> None:
        self._inputs = [Meta(f"{prefix}_in_{index}") for index in range(inputs)]
        self._outputs = [Meta(f"{prefix}_out_{index}") for index in range(outputs)]

    def get_inputs(self):
        return self._inputs

    def get_outputs(self):
        return self._outputs


class MoonshineV1ReferenceTests(unittest.TestCase):
    def test_reviewed_positional_abi_counts_pass(self) -> None:
        names = subject.validate_positional_session_abi(
            Session(1, 1, "pre"),
            Session(2, 1, "enc"),
            Session(3, 25, "uncached"),
            Session(27, 25, "cached"),
        )
        self.assertEqual(["pre_in_0"], names["preprocess_inputs"])
        self.assertEqual(27, len(names["cached_inputs"]))
        self.assertEqual(25, len(names["cached_outputs"]))

    def test_positional_abi_count_drift_fails(self) -> None:
        with self.assertRaisesRegex(subject.ReferenceError, "cached_inputs expected 27"):
            subject.validate_positional_session_abi(
                Session(1, 1, "pre"),
                Session(2, 1, "enc"),
                Session(3, 25, "uncached"),
                Session(26, 25, "cached"),
            )

    def test_output_files_preserve_token_order_and_transcript(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            wav = root / "fixture.wav"
            wav.write_bytes(b"fixture-bytes")
            report = {
                "token_ids": [7, 8, 9],
                "terminated_by_eos": True,
                "decoder_steps": 4,
                "steps": [],
                "binding": "positional",
                "provider": "CPUExecutionProvider",
            }
            output_json = root / "trace.json"
            output_tokens = root / "tokens.txt"
            output_transcript = root / "transcript.txt"

            enriched = subject.write_outputs(
                report,
                wav,
                output_json,
                output_tokens,
                output_transcript,
                "hello world",
            )

            self.assertEqual("7\n8\n9\n", output_tokens.read_text(encoding="utf-8"))
            self.assertEqual("hello world\n", output_transcript.read_text(encoding="utf-8"))
            self.assertEqual(64, len(enriched["audio_sha256"]))
            self.assertEqual(enriched, json.loads(output_json.read_text(encoding="utf-8")))

    def test_generation_constants_match_reviewed_contract(self) -> None:
        self.assertEqual(24, subject.CACHE_STATE_COUNT)
        self.assertEqual(32768, subject.VOCAB_SIZE)
        self.assertEqual(1, subject.DECODER_START_TOKEN_ID)
        self.assertEqual(2, subject.EOS_TOKEN_ID)
        self.assertEqual(194, subject.MAXIMUM_GENERATION_LENGTH)


if __name__ == "__main__":
    unittest.main()
