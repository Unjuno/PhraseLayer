#!/usr/bin/env python3
from __future__ import annotations

import copy
import importlib.util
import pathlib
import unittest

MODULE_PATH = pathlib.Path(__file__).with_name("inspect_marian_onnx_bundle.py")
spec = importlib.util.spec_from_file_location("inspect_marian_onnx_bundle", MODULE_PATH)
subject = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(subject)


def tensor(name, dtype, shape):
    return {"name": name, "dtype": dtype, "shape": list(shape)}


def reviewed_bundle(include_with_past_cross=False):
    encoder = {
        "name": "encoder_model.onnx",
        "inputs": [
            tensor("input_ids", "INT64", ["batch", "encoder_sequence"]),
            tensor("attention_mask", "INT64", ["batch", "encoder_sequence"]),
        ],
        "outputs": [
            tensor("last_hidden_state", "FLOAT", ["batch", "encoder_sequence", 512])
        ],
    }
    decoder = {
        "name": "decoder_model.onnx",
        "inputs": [
            tensor("input_ids", "INT64", ["batch", "decoder_sequence"]),
            tensor("encoder_hidden_states", "FLOAT", ["batch", "encoder_sequence", 512]),
            tensor("encoder_attention_mask", "INT64", ["batch", "encoder_sequence"]),
        ],
        "outputs": [tensor("logits", "FLOAT", ["batch", "decoder_sequence", 46276])],
    }
    decoder_with_past = {
        "name": "decoder_with_past_model.onnx",
        "inputs": [
            tensor("input_ids", "INT64", ["batch", 1]),
            tensor("encoder_hidden_states", "FLOAT", ["batch", "encoder_sequence", 512]),
            tensor("encoder_attention_mask", "INT64", ["batch", "encoder_sequence"]),
        ],
        "outputs": [tensor("logits", "FLOAT", ["batch", 1, 46276])],
    }

    for layer in range(6):
        for attention in ("decoder", "encoder"):
            decoder["outputs"].append(
                tensor(f"present.{layer}.{attention}.key", "FLOAT", ["batch", 8, "time", 64])
            )
            decoder["outputs"].append(
                tensor(f"present.{layer}.{attention}.value", "FLOAT", ["batch", 8, "time", 64])
            )
            decoder_with_past["inputs"].append(
                tensor(
                    f"past_key_values.{layer}.{attention}.key",
                    "FLOAT",
                    ["batch", 8, "time", 64],
                )
            )
            decoder_with_past["inputs"].append(
                tensor(
                    f"past_key_values.{layer}.{attention}.value",
                    "FLOAT",
                    ["batch", 8, "time", 64],
                )
            )

        decoder_with_past["outputs"].append(
            tensor(f"present.{layer}.decoder.key", "FLOAT", ["batch", 8, "time", 64])
        )
        decoder_with_past["outputs"].append(
            tensor(f"present.{layer}.decoder.value", "FLOAT", ["batch", 8, "time", 64])
        )
        if include_with_past_cross:
            decoder_with_past["outputs"].append(
                tensor(f"present.{layer}.encoder.key", "FLOAT", ["batch", 8, "encoder_sequence", 64])
            )
            decoder_with_past["outputs"].append(
                tensor(f"present.{layer}.encoder.value", "FLOAT", ["batch", 8, "encoder_sequence", 64])
            )

    return {
        "encoder_model.onnx": encoder,
        "decoder_model.onnx": decoder,
        "decoder_with_past_model.onnx": decoder_with_past,
    }


class MarianOnnxInspectorTests(unittest.TestCase):
    def test_reviewed_bundle_passes(self):
        report = subject.validate_bundle_manifest(reviewed_bundle())
        self.assertEqual(6, report["decoder_layers"])
        self.assertEqual(8, report["attention_heads"])
        self.assertEqual(64, report["head_dimension"])
        self.assertFalse(report["decoder_with_past_returns_cross_attention_cache"])

    def test_with_past_cross_cache_all_or_none_is_supported(self):
        report = subject.validate_bundle_manifest(reviewed_bundle(include_with_past_cross=True))
        self.assertTrue(report["decoder_with_past_returns_cross_attention_cache"])

    def test_missing_cache_fails(self):
        bundle = reviewed_bundle()
        bundle["decoder_with_past_model.onnx"]["inputs"] = [
            item
            for item in bundle["decoder_with_past_model.onnx"]["inputs"]
            if item["name"] != "past_key_values.5.decoder.key"
        ]
        with self.assertRaisesRegex(subject.ContractError, "past_key_values.5.decoder.key"):
            subject.validate_bundle_manifest(bundle)

    def test_wrong_head_count_fails(self):
        bundle = reviewed_bundle()
        for item in bundle["decoder_model.onnx"]["outputs"]:
            if item["name"] == "present.2.decoder.key":
                item["shape"][1] = 16
        with self.assertRaisesRegex(subject.ContractError, "axis 1 expected 8"):
            subject.validate_bundle_manifest(bundle)

    def test_wrong_vocab_dimension_fails(self):
        bundle = reviewed_bundle()
        bundle["decoder_model.onnx"]["outputs"][0]["shape"][2] = 46277
        with self.assertRaisesRegex(subject.ContractError, "axis 2 expected 46276"):
            subject.validate_bundle_manifest(bundle)

    def test_unexpected_cache_layer_fails(self):
        bundle = reviewed_bundle()
        bundle["decoder_with_past_model.onnx"]["inputs"].append(
            tensor("past_key_values.6.decoder.key", "FLOAT", ["batch", 8, "time", 64])
        )
        with self.assertRaisesRegex(subject.ContractError, "unexpected layer 6"):
            subject.validate_bundle_manifest(bundle)

    def test_partial_with_past_cross_outputs_fail(self):
        bundle = reviewed_bundle(include_with_past_cross=True)
        bundle["decoder_with_past_model.onnx"]["outputs"] = [
            item
            for item in bundle["decoder_with_past_model.onnx"]["outputs"]
            if item["name"] != "present.4.encoder.value"
        ]
        with self.assertRaisesRegex(subject.ContractError, "all-or-none"):
            subject.validate_bundle_manifest(bundle)


if __name__ == "__main__":
    unittest.main()
