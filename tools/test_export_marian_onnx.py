#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import importlib.metadata
import importlib.util
import json
import pathlib
import tempfile
import unittest

MODULE_PATH = pathlib.Path(__file__).with_name("export_marian_onnx.py")
spec = importlib.util.spec_from_file_location("export_marian_onnx", MODULE_PATH)
subject = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(subject)

REVISION = "a863894cdd2b80f3bc1c5966734aee9ffec207d1"
EVIDENCE_RELATIVE = f"models/evidence/opus-mt-en-jap.{REVISION}.snapshot.json"
METADATA_NAMES = [
    "README.md",
    "config.json",
    "generation_config.json",
    "tokenizer_config.json",
    "source.spm",
    "target.spm",
    "vocab.json",
]


def fingerprint(name: str, data: bytes):
    return {"name": name, "size_bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def create_fixture(root: pathlib.Path):
    source_dir = root / "snapshot"
    source_dir.mkdir(parents=True)
    payloads = {name: ("fixture:" + name).encode("utf-8") for name in METADATA_NAMES}
    payloads["pytorch_model.bin"] = b"synthetic-reviewed-weight"
    for name, data in payloads.items():
        (source_dir / name).write_bytes(data)

    metadata = [fingerprint(name, payloads[name]) for name in METADATA_NAMES]
    weight = fingerprint("pytorch_model.bin", payloads["pytorch_model.bin"])
    evidence = {
        "model_id": "Helsinki-NLP/opus-mt-en-jap",
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
        "artifacts": metadata,
        "staging": {
            "allow_list": list(METADATA_NAMES),
            "mode": "huggingface-small-artifacts-only",
            "weights_downloaded": False,
        },
    }
    lock = {
        "candidates": [
            {
                "id": "opus-mt-en-jap",
                "upstream": "Helsinki-NLP/opus-mt-en-jap",
                "revision": REVISION,
                "license": "Apache-2.0",
                "bundled": False,
                "evidence_manifest": EVIDENCE_RELATIVE,
                "metadata_snapshot_artifacts": [
                    {
                        "artifact": item["name"],
                        "artifact_size_bytes": item["size_bytes"],
                        "artifact_sha256": item["sha256"],
                    }
                    for item in metadata
                ],
                "source_weight_artifact": {
                    "artifact": weight["name"],
                    "artifact_size_bytes": weight["size_bytes"],
                    "artifact_sha256": weight["sha256"],
                },
            }
        ]
    }
    lock_path = root / "models" / "models.lock.json"
    evidence_path = root / EVIDENCE_RELATIVE
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    evidence_path.parent.mkdir(parents=True, exist_ok=True)
    lock_path.write_text(json.dumps(lock), encoding="utf-8")
    evidence_path.write_text(json.dumps(evidence), encoding="utf-8")
    return source_dir, lock_path


class MarianOnnxExportTests(unittest.TestCase):
    def test_exact_local_snapshot_passes_lock_bound_validation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            source_dir, lock_path = create_fixture(root)
            report = subject.validate_local_source_snapshot(source_dir, lock_path, root)

        self.assertEqual(REVISION, report["revision"])
        self.assertEqual(7, len(report["metadata_artifacts"]))
        self.assertEqual("pytorch_model.bin", report["weight_artifact"]["name"])

    def test_metadata_drift_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            source_dir, lock_path = create_fixture(root)
            (source_dir / "tokenizer_config.json").write_bytes(b"changed")
            with self.assertRaisesRegex(subject.MarianExportError, "metadata/tokenizer artifact"):
                subject.validate_local_source_snapshot(source_dir, lock_path, root)

    def test_weight_drift_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            source_dir, lock_path = create_fixture(root)
            (source_dir / "pytorch_model.bin").write_bytes(b"wrong weight")
            with self.assertRaisesRegex(subject.MarianExportError, "weight does not match"):
                subject.validate_local_source_snapshot(source_dir, lock_path, root)

    def test_export_command_is_three_graph_offline_baseline_shape(self):
        command = subject.build_export_command(
            pathlib.Path("/snapshot"),
            pathlib.Path("/onnx"),
            "optimum-cli",
        )
        self.assertEqual(
            [
                "optimum-cli",
                "export",
                "onnx",
                "--model",
                "/snapshot",
                "--task",
                "text2text-generation-with-past",
                "--framework",
                "pt",
                "--dtype",
                "fp32",
                "--no-post-process",
                "/onnx",
            ],
            command,
        )

    def test_exact_export_toolchain_passes(self):
        versions = dict(subject.EXPECTED_TOOLCHAIN)
        report = subject.validate_export_toolchain(lambda name: versions[name])
        self.assertEqual(versions, report)

    def test_export_toolchain_drift_is_rejected(self):
        versions = dict(subject.EXPECTED_TOOLCHAIN)
        versions["transformers"] = "5.0.0"
        with self.assertRaisesRegex(subject.MarianExportError, "transformers expected 4.57.6"):
            subject.validate_export_toolchain(lambda name: versions[name])

    def test_missing_export_toolchain_dependency_is_rejected(self):
        def missing(name):
            raise importlib.metadata.PackageNotFoundError(name)

        with self.assertRaisesRegex(subject.MarianExportError, "missing reviewed Marian export dependency"):
            subject.validate_export_toolchain(missing)

    def test_nonempty_output_directory_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = pathlib.Path(temporary) / "onnx"
            output.mkdir()
            (output / "stale.onnx").write_bytes(b"stale")
            with self.assertRaisesRegex(subject.MarianExportError, "must be empty"):
                subject._prepare_output_directory(output)


if __name__ == "__main__":
    unittest.main()
