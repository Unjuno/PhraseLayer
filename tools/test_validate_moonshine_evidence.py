#!/usr/bin/env python3
from __future__ import annotations

import copy
import importlib.util
import pathlib
import unittest

SCRIPT = pathlib.Path(__file__).with_name("validate_moonshine_evidence.py")
SPEC = importlib.util.spec_from_file_location("validate_moonshine_evidence", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)

EVIDENCE = pathlib.Path("models/evidence/moonshine-tiny.390624ed33d594443aa4aa221f5b9f283b545b5a.snapshot.json")


class MoonshineEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.reviewed = subject.load_and_validate(EVIDENCE)

    def test_committed_evidence_passes(self) -> None:
        subject.validate_evidence(copy.deepcopy(self.reviewed))

    def test_revision_or_license_tamper_fails(self) -> None:
        changed = copy.deepcopy(self.reviewed)
        changed["revision"] = "a" * 40
        with self.assertRaisesRegex(subject.EvidenceError, "revision"):
            subject.validate_evidence(changed)

        changed = copy.deepcopy(self.reviewed)
        changed["license"] = "apache-2.0"
        with self.assertRaisesRegex(subject.EvidenceError, "license"):
            subject.validate_evidence(changed)

    def test_artifact_hash_or_size_tamper_fails(self) -> None:
        changed = copy.deepcopy(self.reviewed)
        changed["artifacts"][0]["sha256"] = "0" * 64
        with self.assertRaisesRegex(subject.EvidenceError, "SHA-256 drift"):
            subject.validate_evidence(changed)

        changed = copy.deepcopy(self.reviewed)
        changed["artifacts"][1]["size_bytes"] += 1
        with self.assertRaisesRegex(subject.EvidenceError, "size drift"):
            subject.validate_evidence(changed)

    def test_tokenizer_or_weight_policy_tamper_fails(self) -> None:
        changed = copy.deepcopy(self.reviewed)
        changed["tokenizer_contract"]["unique_token_id_count"] = 32767
        with self.assertRaisesRegex(subject.EvidenceError, "tokenizer contract"):
            subject.validate_evidence(changed)

        changed = copy.deepcopy(self.reviewed)
        changed["weights_downloaded"] = True
        with self.assertRaisesRegex(subject.EvidenceError, "must not represent"):
            subject.validate_evidence(changed)


if __name__ == "__main__":
    unittest.main()
