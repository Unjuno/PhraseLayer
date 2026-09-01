#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import pathlib
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).with_name("validate_moonshine_snapshot_contract.py")
SPEC = importlib.util.spec_from_file_location("validate_moonshine_snapshot_contract", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)

REVISION = "390624ed33d594443aa4aa221f5b9f283b545b5a"


class MoonshineSnapshotContractTests(unittest.TestCase):
    def test_reviewed_synthetic_snapshot_passes(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            self._write_snapshot(root)

            report = subject.validate_snapshot(root, REVISION)

            self.assertEqual("moonshine-ai/moonshine-tiny", report["model_id"])
            self.assertEqual(REVISION, report["revision"])
            self.assertEqual("mit", report["license"])
            self.assertEqual(16000, report["audio_contract"]["sampling_rate"])
            self.assertEqual(32000, report["tokenizer_contract"]["base_vocabulary_size"])
            self.assertEqual(768, report["tokenizer_contract"]["added_token_entries"])
            self.assertEqual(32768, report["tokenizer_contract"]["unique_token_id_count"])
            self.assertEqual(5, len(report["artifacts"]))
            self.assertFalse(report["weights_downloaded"])
            self.assertTrue(all(len(item["sha256"]) == 64 for item in report["artifacts"]))

    def test_short_or_different_revision_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            self._write_snapshot(root)
            with self.assertRaises(subject.SnapshotContractError):
                subject.validate_snapshot(root, "390624e")
            with self.assertRaises(subject.SnapshotContractError):
                subject.validate_snapshot(root, "a" * 40)

    def test_sample_rate_drift_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            self._write_snapshot(root)
            preprocessor = self._read_json(root / "preprocessor_config.json")
            preprocessor["sampling_rate"] = 48000
            self._write_json(root / "preprocessor_config.json", preprocessor)

            with self.assertRaisesRegex(subject.SnapshotContractError, "sample-rate"):
                subject.validate_snapshot(root, REVISION)

    def test_generation_and_architecture_drift_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            self._write_snapshot(root)
            generation = self._read_json(root / "generation_config.json")
            generation["eos_token_id"] = 3
            self._write_json(root / "generation_config.json", generation)
            with self.assertRaisesRegex(subject.SnapshotContractError, "generation EOS"):
                subject.validate_snapshot(root, REVISION)

        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            self._write_snapshot(root)
            config = self._read_json(root / "config.json")
            config["architectures"] = ["WhisperForConditionalGeneration"]
            self._write_json(root / "config.json", config)
            with self.assertRaisesRegex(subject.SnapshotContractError, "architecture"):
                subject.validate_snapshot(root, REVISION)

    def test_token_id_space_and_license_drift_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            self._write_snapshot(root, total_token_ids=32767)
            with self.assertRaisesRegex(subject.SnapshotContractError, "id-space"):
                subject.validate_snapshot(root, REVISION)

        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            self._write_snapshot(root)
            (root / "README.md").write_text("---\nlicense: apache-2.0\n---\n", encoding="utf-8")
            with self.assertRaisesRegex(subject.SnapshotContractError, "license"):
                subject.validate_snapshot(root, REVISION)

    def test_missing_artifact_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            self._write_snapshot(root)
            (root / "tokenizer.json").unlink()
            with self.assertRaisesRegex(subject.SnapshotContractError, "missing"):
                subject.validate_snapshot(root, REVISION)

    @staticmethod
    def _write_snapshot(root: pathlib.Path, total_token_ids: int = 32768) -> None:
        (root / "README.md").write_text("---\nlanguage: en\nlicense: mit\n---\n", encoding="utf-8")
        MoonshineSnapshotContractTests._write_json(root / "config.json", {
            "architectures": ["MoonshineForConditionalGeneration"],
            "model_type": "moonshine",
            "is_encoder_decoder": True,
            "use_cache": True,
            "vocab_size": 32768,
            "hidden_size": 288,
            "encoder_num_hidden_layers": 6,
            "decoder_num_hidden_layers": 6,
            "encoder_num_attention_heads": 8,
            "decoder_num_attention_heads": 8,
            "max_position_embeddings": 194,
            "bos_token_id": 1,
            "decoder_start_token_id": 1,
            "eos_token_id": 2,
            "pad_token_id": 2,
        })
        MoonshineSnapshotContractTests._write_json(root / "generation_config.json", {
            "bos_token_id": 1,
            "decoder_start_token_id": 1,
            "eos_token_id": 2,
            "pad_token_id": 2,
            "max_length": 194,
        })
        MoonshineSnapshotContractTests._write_json(root / "preprocessor_config.json", {
            "feature_extractor_type": "Wav2Vec2FeatureExtractor",
            "feature_size": 1,
            "sampling_rate": 16000,
            "do_normalize": False,
            "return_attention_mask": True,
            "padding_value": 0.0,
        })
        base_size = min(32000, total_token_ids)
        added = [
            {"id": token_id, "content": f"<extra-{token_id}>"}
            for token_id in range(base_size, total_token_ids)
        ]
        MoonshineSnapshotContractTests._write_json(root / "tokenizer.json", {
            "model": {"vocab": list(range(base_size))},
            "added_tokens": added,
        })

    @staticmethod
    def _read_json(path: pathlib.Path):
        return json.loads(path.read_text(encoding="utf-8"))

    @staticmethod
    def _write_json(path: pathlib.Path, value) -> None:
        path.write_text(json.dumps(value), encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
