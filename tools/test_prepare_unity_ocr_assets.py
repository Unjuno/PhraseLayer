#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
from pathlib import Path

import prepare_unity_ocr_assets as subject


class PrepareUnityOcrAssetsTests(unittest.TestCase):
    def write_fixture(self, root: Path) -> tuple[Path, Path, Path, Path]:
        staged = root / "staged"
        generated = root / "generated"
        unity_project = root / "UnityProject"
        (unity_project / "Assets").mkdir(parents=True)

        detector_bytes = b"detector-fixture-v1"
        recognizer_bytes = b"recognizer-fixture-v1"
        yaml_text = (
            "Global:\n"
            "  model_name: fixture\n"
            "PostProcess:\n"
            "  character_dict:\n"
            "  - A\n"
            "  - B\n"
            "  name: \"CTCLabelDecode\"\n"
            "  use_space_char: true\n"
            "PreProcess:\n"
            "  transform_ops: []\n"
        )
        yaml_bytes = yaml_text.encode("utf-8")
        dictionary_bytes = b"A\nB\n"

        detector_path = staged / "pp-ocrv6-tiny-det" / "inference.onnx"
        recognizer_path = staged / "pp-ocrv6-tiny-rec" / "inference.onnx"
        yaml_path = staged / "pp-ocrv6-tiny-rec" / "inference.yml"
        detector_path.parent.mkdir(parents=True)
        recognizer_path.parent.mkdir(parents=True)
        detector_path.write_bytes(detector_bytes)
        recognizer_path.write_bytes(recognizer_bytes)
        yaml_path.write_bytes(yaml_bytes)

        def digest(payload: bytes) -> str:
            return hashlib.sha256(payload).hexdigest()

        lock = {
            "schema_version": 2,
            "candidates": [
                {
                    "id": "pp-ocrv6-tiny-det",
                    "purpose": "ocr-detection",
                    "upstream": "Fixture/detector",
                    "revision": "1" * 40,
                    "artifact": "inference.onnx",
                    "artifact_size_bytes": len(detector_bytes),
                    "artifact_sha256": digest(detector_bytes),
                },
                {
                    "id": "pp-ocrv6-tiny-rec",
                    "purpose": "ocr-recognition",
                    "upstream": "Fixture/recognizer",
                    "revision": "2" * 40,
                    "artifact": "inference.onnx",
                    "artifact_size_bytes": len(recognizer_bytes),
                    "artifact_sha256": digest(recognizer_bytes),
                    "support_artifacts": [
                        {
                            "purpose": "recognition-export-config",
                            "artifact": "inference.yml",
                            "artifact_size_bytes": len(yaml_bytes),
                            "artifact_sha256": digest(yaml_bytes),
                        }
                    ],
                    "recognition_dictionary": {
                        "source_artifact": "inference.yml",
                        "source_format": "paddle-inference-yaml",
                        "postprocess_name": "CTCLabelDecode",
                        "yaml_path": ["PostProcess", "character_dict"],
                        "use_space_char": True,
                        "raw_token_count": 2,
                        "effective_token_count": 3,
                        "generated_artifact": "ppocr_keys.txt",
                        "generated_artifact_size_bytes": len(dictionary_bytes),
                        "generated_artifact_sha256": digest(dictionary_bytes),
                        "generated_manifest": "ppocr_keys.manifest.json",
                    },
                },
            ],
        }
        lock_path = root / "models.lock.json"
        lock_path.write_text(json.dumps(lock), encoding="utf-8")
        return lock_path, staged, generated, unity_project

    def test_prepare_verifies_generates_and_copies_local_assets(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock, staged, generated, unity_project = self.write_fixture(root)

            result = subject.prepare(
                lock,
                staged,
                generated,
                unity_project,
                Path("Assets/LocalOcrAssets/PaddleOCR"),
            )

            destination = Path(result["destination"])
            self.assertEqual((destination / "detector.onnx").read_bytes(), b"detector-fixture-v1")
            self.assertEqual((destination / "recognizer.onnx").read_bytes(), b"recognizer-fixture-v1")
            self.assertEqual((destination / "ppocr_keys.txt").read_bytes(), b"A\nB\n")
            self.assertTrue((destination / "ppocr_keys.manifest.json").is_file())

            local_manifest_path = destination / subject.LOCAL_MANIFEST_NAME
            local_manifest = json.loads(local_manifest_path.read_text(encoding="utf-8"))
            self.assertEqual(local_manifest["detector"]["revision"], "1" * 40)
            self.assertEqual(local_manifest["recognizer"]["revision"], "2" * 40)
            self.assertEqual(local_manifest["dictionary"]["raw_token_count"], 2)
            self.assertEqual(local_manifest["dictionary"]["effective_token_count"], 3)
            self.assertEqual(
                local_manifest["dictionary"]["sha256"],
                hashlib.sha256(b"A\nB\n").hexdigest(),
            )

    def test_prepare_rejects_corrupted_staged_primary_artifact(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock, staged, generated, unity_project = self.write_fixture(root)
            (staged / "pp-ocrv6-tiny-det" / "inference.onnx").write_bytes(b"tampered")

            with self.assertRaisesRegex(Exception, "size mismatch|SHA-256 mismatch"):
                subject.prepare(
                    lock,
                    staged,
                    generated,
                    unity_project,
                    Path("Assets/LocalOcrAssets/PaddleOCR"),
                )

    def test_prepare_rejects_destination_outside_assets(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock, staged, generated, unity_project = self.write_fixture(root)

            with self.assertRaisesRegex(subject.PrepareError, "must live under.*Assets"):
                subject.prepare(
                    lock,
                    staged,
                    generated,
                    unity_project,
                    Path("LocalOcrAssets/PaddleOCR"),
                )

    def test_prepare_rejects_parent_traversal(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock, staged, generated, unity_project = self.write_fixture(root)

            with self.assertRaisesRegex(subject.PrepareError, "cannot contain '\.\.'"):
                subject.prepare(
                    lock,
                    staged,
                    generated,
                    unity_project,
                    Path("Assets/../Outside"),
                )


if __name__ == "__main__":
    unittest.main()
