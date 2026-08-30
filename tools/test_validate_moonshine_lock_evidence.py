#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import pathlib
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).with_name("validate_moonshine_lock_evidence.py")
SPEC = importlib.util.spec_from_file_location("validate_moonshine_lock_evidence", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)

REVISION = subject.REVISION
EVIDENCE_RELATIVE = f"models/evidence/moonshine-tiny.{REVISION}.snapshot.json"


class MoonshineLockEvidenceTests(unittest.TestCase):
    def test_reviewed_synthetic_lock_and_evidence_pass(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            lock = self._write_fixture(root)
            report = subject.validate_lock_evidence(lock, root)
            self.assertEqual("moonshine-tiny", report["candidate"])
            self.assertEqual(REVISION, report["revision"])
            self.assertEqual(5, report["artifact_count"])
            self.assertEqual(32768, report["tokenizer_id_count"])
            self.assertFalse(report["weights_downloaded"])

    def test_revision_or_artifact_hash_drift_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            lock = self._write_fixture(root)
            payload = json.loads(lock.read_text(encoding="utf-8"))
            payload["candidates"][0]["revision"] = "a" * 40
            lock.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaisesRegex(subject.LockEvidenceError, "revision"):
                subject.validate_lock_evidence(lock, root)

        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            lock = self._write_fixture(root)
            payload = json.loads(lock.read_text(encoding="utf-8"))
            payload["candidates"][0]["metadata_snapshot_artifacts"][0]["artifact_sha256"] = "f" * 64
            lock.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaisesRegex(subject.LockEvidenceError, "exactly match"):
                subject.validate_lock_evidence(lock, root)

    def test_audio_tokenizer_and_weight_policy_drift_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            lock = self._write_fixture(root)
            evidence = root / EVIDENCE_RELATIVE
            payload = json.loads(evidence.read_text(encoding="utf-8"))
            payload["audio_contract"]["sampling_rate"] = 48000
            evidence.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaisesRegex(subject.LockEvidenceError, "audio contract"):
                subject.validate_lock_evidence(lock, root)

        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            lock = self._write_fixture(root)
            evidence = root / EVIDENCE_RELATIVE
            payload = json.loads(evidence.read_text(encoding="utf-8"))
            payload["tokenizer_contract"]["added_token_entries"] = 770
            evidence.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaisesRegex(subject.LockEvidenceError, "tokenizer contract"):
                subject.validate_lock_evidence(lock, root)

        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            lock = self._write_fixture(root)
            evidence = root / EVIDENCE_RELATIVE
            payload = json.loads(evidence.read_text(encoding="utf-8"))
            payload["weights_downloaded"] = True
            evidence.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaisesRegex(subject.LockEvidenceError, "weights_downloaded"):
                subject.validate_lock_evidence(lock, root)

    @staticmethod
    def _write_fixture(root: pathlib.Path) -> pathlib.Path:
        evidence_path = root / EVIDENCE_RELATIVE
        evidence_path.parent.mkdir(parents=True, exist_ok=True)
        artifacts = [
            {"name": "README.md", "size_bytes": 10, "sha256": "1" * 64},
            {"name": "config.json", "size_bytes": 11, "sha256": "2" * 64},
            {"name": "generation_config.json", "size_bytes": 12, "sha256": "3" * 64},
            {"name": "preprocessor_config.json", "size_bytes": 13, "sha256": "4" * 64},
            {"name": "tokenizer.json", "size_bytes": 14, "sha256": "5" * 64},
        ]
        evidence = {
            "schema_version": 1,
            "model_id": "moonshine-ai/moonshine-tiny",
            "revision": REVISION,
            "license": "mit",
            "language": "en",
            "audio_contract": {
                "feature_size": 1,
                "normalize": False,
                "return_attention_mask": True,
                "sampling_rate": 16000,
            },
            "generation_contract": {
                "bos_token_id": 1,
                "decoder_start_token_id": 1,
                "eos_token_id": 2,
                "max_length": 194,
                "pad_token_id": 2,
                "vocabulary_size": 32768,
            },
            "tokenizer_contract": {
                "added_token_entries": 771,
                "base_vocabulary_size": 32000,
                "maximum_token_id": 32767,
                "minimum_token_id": 0,
                "unique_token_id_count": 32768,
            },
            "artifacts": artifacts,
            "staging": {
                "allow_list": subject.EXPECTED_ALLOW_LIST,
                "mode": "huggingface-small-artifacts-only",
                "weights_downloaded": False,
            },
            "weights_downloaded": False,
        }
        evidence_path.write_text(json.dumps(evidence), encoding="utf-8")

        candidate_artifacts = [
            {
                "artifact": item["name"],
                "artifact_size_bytes": item["size_bytes"],
                "artifact_sha256": item["sha256"],
            }
            for item in artifacts
        ]
        lock = {
            "schema_version": 2,
            "candidates": [{
                "id": "moonshine-tiny",
                "upstream": "moonshine-ai/moonshine-tiny",
                "revision": REVISION,
                "evidence_manifest": EVIDENCE_RELATIVE,
                "license": "MIT",
                "sample_rate": 16000,
                "vocab_size": 32768,
                "base_tokenizer_vocab_size": 32000,
                "added_token_entries": 771,
                "decoder_start_token_id": 1,
                "eos_token_id": 2,
                "pad_token_id": 2,
                "max_generation_length": 194,
                "metadata_snapshot_artifacts": candidate_artifacts,
                "bundled": False,
            }],
        }
        lock_path = root / "models/models.lock.json"
        lock_path.parent.mkdir(parents=True, exist_ok=True)
        lock_path.write_text(json.dumps(lock), encoding="utf-8")
        return lock_path


if __name__ == "__main__":
    unittest.main()
