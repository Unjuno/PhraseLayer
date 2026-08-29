#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import pathlib
import tempfile
import unittest

MODULE_PATH = pathlib.Path(__file__).with_name("validate_marian_lock_evidence.py")
spec = importlib.util.spec_from_file_location("validate_marian_lock_evidence", MODULE_PATH)
subject = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(subject)

REVISION = "a863894cdd2b80f3bc1c5966734aee9ffec207d1"
EVIDENCE_PATH = f"models/evidence/opus-mt-en-jap.{REVISION}.snapshot.json"
WEIGHT_EVIDENCE_PATH = f"models/evidence/opus-mt-en-jap.{REVISION}.source-weight.json"
WEIGHT = {"name": "pytorch_model.bin", "size_bytes": 23, "sha256": "a" * 64}


def artifacts():
    return [
        {"name": name, "size_bytes": index + 1, "sha256": f"{index + 1:064x}"}
        for index, name in enumerate(subject.EXPECTED_ALLOW_LIST)
    ]


def evidence():
    return {
        "model_id": subject.UPSTREAM,
        "revision": REVISION,
        "license": "apache-2.0",
        "languages": {"source": "en", "target": "jap"},
        "generation_policy": {
            "bad_word_token_ids": [46275],
            "forced_eos_token_id": 0,
            "phraselayer_parity_beam_width": 1,
            "renormalize_logits": True,
            "upstream_default_beam_width": 4,
        },
        "artifacts": artifacts(),
        "staging": {
            "allow_list": list(subject.EXPECTED_ALLOW_LIST),
            "mode": "huggingface-small-artifacts-only",
            "weights_downloaded": False,
        },
    }


def weight_evidence():
    return {
        "schema_version": 1,
        "model_id": subject.UPSTREAM,
        "revision": REVISION,
        "artifact": dict(WEIGHT),
        "identity_source": "upstream-lfs-pointer-history",
        "local_file_hash_required_before_export": True,
        "weight_downloaded": False,
        "bundled": False,
    }


def lock(evidence_doc=None, weight_doc=None):
    evidence_doc = evidence_doc or evidence()
    weight_doc = weight_doc or weight_evidence()
    artifact = weight_doc["artifact"]
    return {
        "candidates": [
            {
                "id": subject.MODEL_ID,
                "upstream": subject.UPSTREAM,
                "revision": REVISION,
                "license": "Apache-2.0",
                "bundled": False,
                "evidence_manifest": EVIDENCE_PATH,
                "metadata_snapshot_artifacts": [
                    {
                        "artifact": item["name"],
                        "artifact_size_bytes": item["size_bytes"],
                        "artifact_sha256": item["sha256"],
                    }
                    for item in evidence_doc["artifacts"]
                ],
                "source_weight_artifact": {
                    "artifact": artifact["name"],
                    "artifact_size_bytes": artifact["size_bytes"],
                    "artifact_sha256": artifact["sha256"],
                    "evidence_manifest": WEIGHT_EVIDENCE_PATH,
                    "identity_status": subject.EXPECTED_WEIGHT_STATUS,
                },
            }
        ]
    }


def write_fixture(root: pathlib.Path, lock_doc, evidence_doc, weight_doc=None):
    weight_doc = weight_doc or weight_evidence()
    lock_path = root / "models" / "models.lock.json"
    evidence_path = root / EVIDENCE_PATH
    weight_path = root / WEIGHT_EVIDENCE_PATH
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    evidence_path.parent.mkdir(parents=True, exist_ok=True)
    weight_path.parent.mkdir(parents=True, exist_ok=True)
    lock_path.write_text(json.dumps(lock_doc), encoding="utf-8")
    evidence_path.write_text(json.dumps(evidence_doc), encoding="utf-8")
    weight_path.write_text(json.dumps(weight_doc), encoding="utf-8")
    return lock_path


class MarianLockEvidenceTests(unittest.TestCase):
    def test_exact_lock_and_evidence_pass(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            evidence_doc = evidence()
            weight_doc = weight_evidence()
            lock_path = write_fixture(root, lock(evidence_doc, weight_doc), evidence_doc, weight_doc)
            report = subject.validate_lock_evidence(lock_path, root)

        self.assertEqual(REVISION, report["revision"])
        self.assertEqual(7, report["artifact_count"])
        self.assertEqual("pytorch_model.bin", report["source_weight"]["name"])
        self.assertFalse(report["weights_downloaded"])

    def test_revision_mismatch_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            evidence_doc = evidence()
            evidence_doc["revision"] = "b" * 40
            lock_path = write_fixture(root, lock(), evidence_doc)
            with self.assertRaisesRegex(subject.LockEvidenceError, "revision does not match"):
                subject.validate_lock_evidence(lock_path, root)

    def test_artifact_hash_mismatch_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            evidence_doc = evidence()
            lock_doc = lock(evidence_doc)
            lock_doc["candidates"][0]["metadata_snapshot_artifacts"][2]["artifact_sha256"] = "f" * 64
            lock_path = write_fixture(root, lock_doc, evidence_doc)
            with self.assertRaisesRegex(subject.LockEvidenceError, "do not exactly match"):
                subject.validate_lock_evidence(lock_path, root)

    def test_source_weight_hash_mismatch_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            evidence_doc = evidence()
            weight_doc = weight_evidence()
            lock_doc = lock(evidence_doc, weight_doc)
            lock_doc["candidates"][0]["source_weight_artifact"]["artifact_sha256"] = "b" * 64
            lock_path = write_fixture(root, lock_doc, evidence_doc, weight_doc)
            with self.assertRaisesRegex(subject.LockEvidenceError, "does not exactly match"):
                subject.validate_lock_evidence(lock_path, root)

    def test_source_weight_evidence_requires_local_hash(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            evidence_doc = evidence()
            weight_doc = weight_evidence()
            weight_doc["local_file_hash_required_before_export"] = False
            lock_path = write_fixture(root, lock(), evidence_doc, weight_doc)
            with self.assertRaisesRegex(subject.LockEvidenceError, "require local-file hash"):
                subject.validate_lock_evidence(lock_path, root)

    def test_allow_list_drift_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            evidence_doc = evidence()
            evidence_doc["staging"]["allow_list"] = list(subject.EXPECTED_ALLOW_LIST[:-1])
            lock_path = write_fixture(root, lock(), evidence_doc)
            with self.assertRaisesRegex(subject.LockEvidenceError, "allow-list drift"):
                subject.validate_lock_evidence(lock_path, root)

    def test_weight_download_claim_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            evidence_doc = evidence()
            evidence_doc["staging"]["weights_downloaded"] = True
            lock_path = write_fixture(root, lock(), evidence_doc)
            with self.assertRaisesRegex(subject.LockEvidenceError, "weights_downloaded=false"):
                subject.validate_lock_evidence(lock_path, root)

    def test_license_drift_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            evidence_doc = evidence()
            evidence_doc["license"] = "mit"
            lock_path = write_fixture(root, lock(), evidence_doc)
            with self.assertRaisesRegex(subject.LockEvidenceError, "does not match lock license"):
                subject.validate_lock_evidence(lock_path, root)

    def test_missing_evidence_manifest_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            lock_path = root / "models" / "models.lock.json"
            lock_path.parent.mkdir(parents=True, exist_ok=True)
            lock_path.write_text(json.dumps(lock()), encoding="utf-8")
            with self.assertRaisesRegex(subject.LockEvidenceError, "source-weight evidence manifest does not exist|evidence manifest does not exist"):
                subject.validate_lock_evidence(lock_path, root)


if __name__ == "__main__":
    unittest.main()
