#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import importlib.util
import json
import pathlib
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).with_name("prepare_unity_moonshine_v1_assets.py")
SPEC = importlib.util.spec_from_file_location("prepare_unity_moonshine_v1_assets", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)


class MoonshineUnityAssetStagingTests(unittest.TestCase):
    def test_verified_bundle_is_staged_and_manifested(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            snapshot, evidence = self._fixture(root)
            destination = root / "Assets/LocalAsrAssets/MoonshineV1Tiny"
            manifest = destination / "moonshine-v1-tiny.staging.json"

            report = subject.prepare(snapshot, destination, manifest, evidence)

            self.assertEqual(subject.EXPECTED_REVISION, report["revision"])
            self.assertEqual(
                ["preprocess.onnx", "encode.onnx", "uncached_decode.onnx", "cached_decode.onnx"],
                report["staged_graphs"],
            )
            self.assertTrue(report["token_decoder_required"])
            self.assertTrue(manifest.is_file())
            for name in report["staged_graphs"]:
                self.assertTrue((destination / name).is_file())

    def test_modified_local_graph_is_rejected_before_copy(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            snapshot, evidence = self._fixture(root)
            graph = snapshot / subject.EXPECTED_ARTIFACTS[2]
            graph.write_bytes(graph.read_bytes() + b"drift")
            destination = root / "Assets/LocalAsrAssets/MoonshineV1Tiny"

            with self.assertRaisesRegex(subject.PrepareError, "identity mismatch"):
                subject.prepare(snapshot, destination, destination / "manifest.json", evidence)
            self.assertFalse(destination.exists())

    def test_evidence_revision_binding_and_artifact_order_drift_are_rejected(self) -> None:
        for mutation, message in (
            (lambda payload: payload.__setitem__("revision", "a" * 40), "revision"),
            (lambda payload: payload.__setitem__("binding", "named"), "binding"),
            (lambda payload: payload["artifacts"].reverse(), "order/set"),
        ):
            with self.subTest(message=message), tempfile.TemporaryDirectory() as raw:
                root = pathlib.Path(raw)
                snapshot, evidence = self._fixture(root)
                payload = json.loads(evidence.read_text(encoding="utf-8"))
                mutation(payload)
                evidence.write_text(json.dumps(payload), encoding="utf-8")
                with self.assertRaisesRegex(subject.PrepareError, message):
                    subject.prepare(snapshot, root / "stage", root / "stage/manifest.json", evidence)

    def test_corrupt_evidence_hash_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            root = pathlib.Path(raw)
            snapshot, evidence = self._fixture(root)
            payload = json.loads(evidence.read_text(encoding="utf-8"))
            payload["artifacts"][0]["sha256"] = "z" * 64
            evidence.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaisesRegex(subject.PrepareError, "sha256"):
                subject.prepare(snapshot, root / "stage", root / "stage/manifest.json", evidence)

    @staticmethod
    def _fixture(root: pathlib.Path) -> tuple[pathlib.Path, pathlib.Path]:
        snapshot = root / "snapshot"
        artifacts = []
        for index, relative_name in enumerate(subject.EXPECTED_ARTIFACTS):
            path = snapshot / relative_name
            path.parent.mkdir(parents=True, exist_ok=True)
            data = (f"synthetic-moonshine-graph-{index}".encode("utf-8")) * (index + 1)
            path.write_bytes(data)
            artifacts.append({
                "name": relative_name,
                "size_bytes": len(data),
                "sha256": hashlib.sha256(data).hexdigest(),
            })

        evidence_payload = {
            "schema_version": 1,
            "model_id": subject.EXPECTED_MODEL_ID,
            "revision": subject.EXPECTED_REVISION,
            "variant": "tiny",
            "bundle_kind": "moonshine-v1-four-graph",
            "binding": "positional",
            "hidden_size": 288,
            "vocabulary_size": 32768,
            "cache_state_count": 24,
            "decoder_attention_heads": 8,
            "decoder_head_dimension": 36,
            "artifacts": artifacts,
        }
        evidence = root / "evidence.json"
        evidence.write_text(json.dumps(evidence_payload), encoding="utf-8")
        return snapshot, evidence


if __name__ == "__main__":
    unittest.main()
