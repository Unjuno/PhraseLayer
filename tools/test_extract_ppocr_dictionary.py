#!/usr/bin/env python3
from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

import extract_ppocr_dictionary as subject


class ExtractPpOcrDictionaryTests(unittest.TestCase):
    def make_lock(self, root: Path, *, use_space_char: bool = True) -> Path:
        lock = {
            "schema_version": 2,
            "candidates": [
                {
                    "id": "pp-ocrv6-tiny-rec",
                    "purpose": "ocr-recognition",
                    "upstream": "PaddlePaddle/PP-OCRv6_tiny_rec_onnx",
                    "revision": "2612ab37152ae0a677521bae4e1e3d4fb4cf7c30",
                    "artifact": "inference.onnx",
                    "support_artifacts": [
                        {
                            "purpose": "recognition-export-metadata",
                            "artifact": "inference.json",
                            "artifact_size_bytes": None,
                            "artifact_sha256": None,
                        }
                    ],
                    "recognition_dictionary": {
                        "source_artifact": "inference.json",
                        "postprocess_name": "CTCLabelDecode",
                        "json_path": ["PostProcess", "character_dict"],
                        "use_space_char": use_space_char,
                        "generated_artifact": "ppocr_keys.txt",
                        "generated_manifest": "ppocr_keys.manifest.json",
                    },
                }
            ],
        }
        path = root / "models.lock.json"
        path.write_text(json.dumps(lock), encoding="utf-8")
        return path

    def write_metadata(self, root: Path, tokens: list[object], *, name: str = "CTCLabelDecode") -> Path:
        path = root / "inference.json"
        path.write_text(
            json.dumps(
                {"PostProcess": {"name": name, "character_dict": tokens}},
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )
        return path

    def test_exports_raw_tokens_exactly_and_records_effective_space_token(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = self.make_lock(root, use_space_char=True)
            metadata = self.write_metadata(root, ["A", "", "中", "  "])
            output = root / "out"

            dictionary_path, manifest_path, manifest = subject.export_dictionary(
                lock,
                "pp-ocrv6-tiny-rec",
                metadata,
                output,
            )

            self.assertEqual(dictionary_path.read_text(encoding="utf-8"), "A\n\n中\n  \n")
            self.assertTrue(manifest_path.is_file())
            self.assertEqual(manifest["raw_token_count"], 4)
            self.assertEqual(manifest["effective_token_count"], 5)
            self.assertTrue(manifest["use_space_char"])
            self.assertFalse(manifest["raw_contains_literal_space"])
            self.assertEqual(len(manifest["generated_sha256"]), 64)

    def test_literal_space_is_allowed_when_use_space_char_is_false(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = self.make_lock(root, use_space_char=False)
            metadata = self.write_metadata(root, ["A", " "])

            _, _, manifest = subject.export_dictionary(
                lock,
                "pp-ocrv6-tiny-rec",
                metadata,
                root / "out",
            )

            self.assertTrue(manifest["raw_contains_literal_space"])
            self.assertEqual(manifest["effective_token_count"], 2)

    def test_rejects_literal_space_when_use_space_char_would_append_another(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = self.make_lock(root, use_space_char=True)
            metadata = self.write_metadata(root, ["A", " "])

            with self.assertRaisesRegex(subject.DictionaryExportError, "another space token"):
                subject.export_dictionary(
                    lock,
                    "pp-ocrv6-tiny-rec",
                    metadata,
                    root / "out",
                )

    def test_rejects_newline_token(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = self.make_lock(root)
            metadata = self.write_metadata(root, ["A", "x\ny"])

            with self.assertRaisesRegex(subject.DictionaryExportError, "contains a newline"):
                subject.export_dictionary(
                    lock,
                    "pp-ocrv6-tiny-rec",
                    metadata,
                    root / "out",
                )

    def test_rejects_postprocess_drift(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = self.make_lock(root)
            metadata = self.write_metadata(root, ["A"], name="AttnLabelDecode")

            with self.assertRaisesRegex(subject.DictionaryExportError, "postprocess mismatch"):
                subject.export_dictionary(
                    lock,
                    "pp-ocrv6-tiny-rec",
                    metadata,
                    root / "out",
                )

    def test_rejects_source_artifact_not_locked_as_support(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock_payload = json.loads(self.make_lock(root).read_text(encoding="utf-8"))
            lock_payload["candidates"][0]["support_artifacts"] = []
            lock = root / "models.lock.json"
            lock.write_text(json.dumps(lock_payload), encoding="utf-8")
            metadata = self.write_metadata(root, ["A"])

            with self.assertRaisesRegex(subject.DictionaryExportError, "support_artifacts"):
                subject.export_dictionary(
                    lock,
                    "pp-ocrv6-tiny-rec",
                    metadata,
                    root / "out",
                )


if __name__ == "__main__":
    unittest.main()
