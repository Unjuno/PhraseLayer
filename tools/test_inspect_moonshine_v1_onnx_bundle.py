#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import pathlib
import unittest

SCRIPT = pathlib.Path(__file__).with_name("inspect_moonshine_v1_onnx_bundle.py")
SPEC = importlib.util.spec_from_file_location("inspect_moonshine_v1_onnx_bundle", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)


def tensor(name, dtype, shape):
    return {"name": name, "dtype": dtype, "shape": shape}


def reviewed_bundle():
    states = [tensor(f"state_{i}", "FLOAT", [1, 8, None, 36]) for i in range(24)]
    return {
        "preprocess.onnx": {
            "name": "preprocess.onnx",
            "inputs": [tensor("audio", "FLOAT", [1, None])],
            "outputs": [tensor("features", "FLOAT", [1, None, 288])],
        },
        "encode.onnx": {
            "name": "encode.onnx",
            "inputs": [
                tensor("features", "FLOAT", [1, None, 288]),
                tensor("features_len", "INT32", [1]),
            ],
            "outputs": [tensor("encoder_out", "FLOAT", [1, None, 288])],
        },
        "uncached_decode.onnx": {
            "name": "uncached_decode.onnx",
            "inputs": [
                tensor("token", "INT32", [1, 1]),
                tensor("encoder_out", "FLOAT", [1, None, 288]),
                tensor("token_len", "INT32", [1]),
            ],
            "outputs": [tensor("logits", "FLOAT", [1, 1, 32768]), *states],
        },
        "cached_decode.onnx": {
            "name": "cached_decode.onnx",
            "inputs": [
                tensor("token", "INT32", [1, 1]),
                tensor("encoder_out", "FLOAT", [1, None, 288]),
                tensor("token_len", "INT32", [1]),
                *states,
            ],
            "outputs": [
                tensor("logits", "FLOAT", [1, 1, 32768]),
                *[tensor(f"next_state_{i}", "FLOAT", [1, 8, None, 36]) for i in range(24)],
            ],
        },
    }


class MoonshineV1OnnxInspectorTests(unittest.TestCase):
    def test_reviewed_bundle_passes(self):
        report = subject.validate_bundle_manifest(reviewed_bundle())
        self.assertEqual(24, report["cache_state_count"])
        self.assertEqual(288, report["hidden_size"])
        self.assertEqual(32768, report["vocabulary_size"])
        self.assertEqual("positional", report["binding"])

    def test_missing_graph_fails(self):
        bundle = reviewed_bundle()
        del bundle["cached_decode.onnx"]
        with self.assertRaisesRegex(subject.ContractError, "missing Moonshine"):
            subject.validate_bundle_manifest(bundle)

    def test_decoder_state_count_drift_fails(self):
        bundle = reviewed_bundle()
        bundle["cached_decode.onnx"]["inputs"].pop()
        with self.assertRaisesRegex(subject.ContractError, "27 inputs/25 outputs"):
            subject.validate_bundle_manifest(bundle)

    def test_dtype_or_rank_drift_fails(self):
        bundle = reviewed_bundle()
        bundle["uncached_decode.onnx"]["inputs"][0]["dtype"] = "INT64"
        with self.assertRaisesRegex(subject.ContractError, "dtype expected INT32"):
            subject.validate_bundle_manifest(bundle)

        bundle = reviewed_bundle()
        bundle["cached_decode.onnx"]["outputs"][1]["shape"] = [1, 1, 1]
        with self.assertRaisesRegex(subject.ContractError, "rank expected 4"):
            subject.validate_bundle_manifest(bundle)

    def test_known_hidden_and_vocab_dimensions_are_checked(self):
        bundle = reviewed_bundle()
        bundle["encode.onnx"]["outputs"][0]["shape"][2] = 512
        with self.assertRaisesRegex(subject.ContractError, "expected 288"):
            subject.validate_bundle_manifest(bundle)

        bundle = reviewed_bundle()
        bundle["uncached_decode.onnx"]["outputs"][0]["shape"][2] = 32000
        with self.assertRaisesRegex(subject.ContractError, "expected 32768"):
            subject.validate_bundle_manifest(bundle)


if __name__ == "__main__":
    unittest.main()
