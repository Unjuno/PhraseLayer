#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import pathlib
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).with_name("validate_moonshine_local_source.py")
SPEC = importlib.util.spec_from_file_location("validate_moonshine_local_source", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)

ROOT = pathlib.Path(__file__).resolve().parents[1]
LOCK = ROOT / "models/models.lock.json"


class MoonshineLocalSourceTests(unittest.TestCase):
    @staticmethod
    def _expected_by_name():
        lock = json.loads(LOCK.read_text(encoding="utf-8"))
        candidate = next(item for item in lock["candidates"] if item["id"] == "moonshine-tiny")
        evidence = json.loads((ROOT / candidate["evidence_manifest"]).read_text(encoding="utf-8"))
        expected = {item["name"]: item for item in evidence["artifacts"]}
        weight = candidate["source_weight_artifact"]
        expected[weight["artifact"]] = {
            "name": weight["artifact"],
            "size_bytes": weight["artifact_size_bytes"],
            "sha256": weight["artifact_sha256"],
        }
        return expected

    def test_exact_locked_source_passes_without_reading_large_weight(self):
        expected = self._expected_by_name()
        with tempfile.TemporaryDirectory() as raw:
            source_dir = pathlib.Path(raw)
            report = subject.validate_local_source(
                source_dir,
                LOCK,
                ROOT,
                fingerprint_reader=lambda path: expected[path.name],
            )
        self.assertTrue(report["ready_for_export"])
        self.assertEqual("model.safetensors", report["weight_artifact"]["name"])
        self.assertEqual(108389192, report["weight_artifact"]["size_bytes"])

    def test_weight_hash_drift_is_rejected(self):
        expected = self._expected_by_name()
        with tempfile.TemporaryDirectory() as raw:
            source_dir = pathlib.Path(raw)

            def changed(path):
                value = dict(expected[path.name])
                if path.name == "model.safetensors":
                    value["sha256"] = "f" * 64
                return value

            with self.assertRaisesRegex(subject.LocalSourceError, "safetensors weight"):
                subject.validate_local_source(source_dir, LOCK, ROOT, fingerprint_reader=changed)

    def test_metadata_hash_drift_is_rejected(self):
        expected = self._expected_by_name()
        with tempfile.TemporaryDirectory() as raw:
            source_dir = pathlib.Path(raw)

            def changed(path):
                value = dict(expected[path.name])
                if path.name == "config.json":
                    value["sha256"] = "e" * 64
                return value

            with self.assertRaisesRegex(subject.LocalSourceError, "config.json"):
                subject.validate_local_source(source_dir, LOCK, ROOT, fingerprint_reader=changed)


if __name__ == "__main__":
    unittest.main()
