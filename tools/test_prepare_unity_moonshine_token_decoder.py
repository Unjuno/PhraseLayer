#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import importlib.util
import json
import pathlib
import tempfile
import types
import unittest
from unittest import mock

SCRIPT = pathlib.Path(__file__).with_name("prepare_unity_moonshine_token_decoder.py")
SPEC = importlib.util.spec_from_file_location("prepare_unity_moonshine_token_decoder", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)


class UnityMoonshineTokenDecoderTests(unittest.TestCase):
    def test_verified_tokenizer_is_delegated_and_resource_manifest_is_bound(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            snapshot, evidence = self._fixture(root, b'{"synthetic":true}')
            output = root / "Resources/LocalAsrAssets/moonshine-tiny.tokens.bytes"
            manifest = root / "Resources/LocalAsrAssets/moonshine-tiny.tokens.manifest.json"

            calls = []
            def fake_prepare(tokenizer_path, output_path, manifest_path):
                calls.append((tokenizer_path, output_path, manifest_path))
                output_path.parent.mkdir(parents=True, exist_ok=True)
                output_path.write_bytes(b"decoder")
                payload = {
                    "schema_version": 1,
                    "format": "moonshine-bin-tokenizer-compatible-v1",
                    "token_count": 32768,
                    "source_tokenizer_sha256": hashlib.sha256(tokenizer_path.read_bytes()).hexdigest(),
                    "source_tokenizer_size_bytes": tokenizer_path.stat().st_size,
                    "artifact": output_path.name,
                    "artifact_size_bytes": 7,
                    "artifact_sha256": hashlib.sha256(b"decoder").hexdigest(),
                }
                manifest_path.parent.mkdir(parents=True, exist_ok=True)
                manifest_path.write_text(json.dumps(payload), encoding="utf-8")
                return payload

            with mock.patch.object(subject, "_load_module", return_value=types.SimpleNamespace(prepare=fake_prepare)):
                report = subject.prepare(snapshot, output, manifest, evidence)

            self.assertEqual(1, len(calls))
            self.assertTrue(output.is_file())
            self.assertTrue(report["source_tokenizer_verified"])
            self.assertFalse(report["weights_required"])
            self.assertEqual("LocalAsrAssets/moonshine-tiny.tokens", report["unity_resource_path"])
            self.assertEqual(report, json.loads(manifest.read_text(encoding="utf-8")))

    def test_local_tokenizer_identity_drift_is_rejected_before_generation(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            snapshot, evidence = self._fixture(root, b"pinned")
            (snapshot / "tokenizer.json").write_bytes(b"different")
            with mock.patch.object(subject, "_load_module") as loader:
                with self.assertRaisesRegex(subject.PrepareError, "does not match"):
                    subject.prepare(snapshot, root / "out.bytes", root / "manifest.json", evidence)
                loader.assert_not_called()

    def test_evidence_revision_or_tokenizer_cardinality_drift_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            snapshot, evidence = self._fixture(root, b"tokenizer")
            payload = json.loads(evidence.read_text(encoding="utf-8"))
            payload["revision"] = "a" * 40
            evidence.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaisesRegex(subject.PrepareError, "revision"):
                subject.prepare(snapshot, root / "out.bytes", root / "manifest.json", evidence)

        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            snapshot, evidence = self._fixture(root, b"tokenizer")
            payload = json.loads(evidence.read_text(encoding="utf-8"))
            payload["artifacts"].append(dict(payload["artifacts"][0]))
            evidence.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaisesRegex(subject.PrepareError, "exactly one"):
                subject.prepare(snapshot, root / "out.bytes", root / "manifest.json", evidence)

    @staticmethod
    def _fixture(root: pathlib.Path, tokenizer_bytes: bytes) -> tuple[pathlib.Path, pathlib.Path]:
        snapshot = root / "snapshot"
        snapshot.mkdir(parents=True)
        (snapshot / "tokenizer.json").write_bytes(tokenizer_bytes)
        evidence = root / "evidence.json"
        evidence.write_text(json.dumps({
            "model_id": "moonshine-ai/moonshine-tiny",
            "revision": subject.SOURCE_REVISION,
            "artifacts": [{
                "name": "tokenizer.json",
                "size_bytes": len(tokenizer_bytes),
                "sha256": hashlib.sha256(tokenizer_bytes).hexdigest(),
            }],
        }), encoding="utf-8")
        return snapshot, evidence


if __name__ == "__main__":
    unittest.main()
