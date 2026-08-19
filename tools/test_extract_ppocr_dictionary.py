#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import json
import tempfile
import unittest
from pathlib import Path

import extract_ppocr_dictionary as subject


class ExtractPpOcrDictionaryTests(unittest.TestCase):
    def make_lock(
        self,
        root: Path,
        tokens: list[str],
        *,
        use_space_char: bool = True,
        raw_token_count: int | None = None,
        effective_token_count: int | None = None,
        generated_size: int | None = None,
        generated_sha256: str | None = None,
    ) -> Path:
        output = subject.dictionary_bytes(tokens)
        raw_count = len(tokens) if raw_token_count is None else raw_token_count
        effective_count = (
            len(tokens) + (1 if use_space_char else 0)
            if effective_token_count is None
            else effective_token_count
        )
        size = len(output) if generated_size is None else generated_size
        digest = hashlib.sha256(output).hexdigest() if generated_sha256 is None else generated_sha256

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
                            "purpose": "recognition-export-config",
                            "artifact": "inference.yml",
                            "artifact_size_bytes": 123,
                            "artifact_sha256": "0" * 64,
                        }
                    ],
                    "recognition_dictionary": {
                        "source_artifact": "inference.yml",
                        "source_format": "paddle-inference-yaml",
                        "postprocess_name": "CTCLabelDecode",
                        "yaml_path": ["PostProcess", "character_dict"],
                        "use_space_char": use_space_char,
                        "raw_token_count": raw_count,
                        "effective_token_count": effective_count,
                        "generated_artifact": "ppocr_keys.txt",
                        "generated_artifact_size_bytes": size,
                        "generated_artifact_sha256": digest,
                        "generated_manifest": "ppocr_keys.manifest.json",
                    },
                }
            ],
        }
        path = root / "models.lock.json"
        path.write_text(json.dumps(lock), encoding="utf-8")
        return path

    def write_metadata(
        self,
        root: Path,
        serialized_tokens: list[str],
        *,
        name: str = "CTCLabelDecode",
    ) -> Path:
        path = root / "inference.yml"
        lines = [
            "Global:",
            "  model_name: fixture",
            "PostProcess:",
            "  character_dict:",
        ]
        lines.extend("  - " + token for token in serialized_tokens)
        lines.extend(
            [
                "  name: " + json.dumps(name),
                "  use_space_char: true",
                "PreProcess:",
                "  transform_ops: []",
            ]
        )
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        return path

    def test_exports_yaml_tokens_exactly_and_records_effective_space_token(self) -> None:
        decoded = ["A", "", "中", "  "]
        serialized = [json.dumps(token, ensure_ascii=False) for token in decoded]
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = self.make_lock(root, decoded, use_space_char=True)
            metadata = self.write_metadata(root, serialized)
            output = root / "out"

            dictionary_path, manifest_path, manifest = subject.export_dictionary(
                lock,
                "pp-ocrv6-tiny-rec",
                metadata,
                output,
            )

            self.assertEqual(dictionary_path.read_text(encoding="utf-8"), "A\n\n中\n  \n")
            self.assertTrue(manifest_path.is_file())
            self.assertEqual(manifest["source_artifact"], "inference.yml")
            self.assertEqual(manifest["source_format"], "paddle-inference-yaml")
            self.assertEqual(manifest["raw_token_count"], 4)
            self.assertEqual(manifest["effective_token_count"], 5)
            self.assertTrue(manifest["use_space_char"])
            self.assertFalse(manifest["raw_contains_literal_space"])
            self.assertEqual(manifest["generated_size_bytes"], len(dictionary_path.read_bytes()))
            self.assertEqual(len(manifest["generated_sha256"]), 64)

    def test_parses_paddle_single_quoted_apostrophe(self) -> None:
        decoded = ["'"]
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = self.make_lock(root, decoded)
            metadata = self.write_metadata(root, ["''''"])

            dictionary_path, _, _ = subject.export_dictionary(
                lock, "pp-ocrv6-tiny-rec", metadata, root / "out"
            )

            self.assertEqual(dictionary_path.read_text(encoding="utf-8"), "'\n")

    def test_preserves_unquoted_backslash_scalar(self) -> None:
        decoded = ["\\"]
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = self.make_lock(root, decoded)
            metadata = self.write_metadata(root, ["\\"])

            dictionary_path, _, _ = subject.export_dictionary(
                lock, "pp-ocrv6-tiny-rec", metadata, root / "out"
            )

            self.assertEqual(dictionary_path.read_text(encoding="utf-8"), "\\\n")

    def test_literal_space_is_allowed_when_use_space_char_is_false(self) -> None:
        decoded = ["A", " "]
        serialized = [json.dumps(token) for token in decoded]
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = self.make_lock(root, decoded, use_space_char=False)
            metadata = self.write_metadata(root, serialized)

            _, _, manifest = subject.export_dictionary(
                lock,
                "pp-ocrv6-tiny-rec",
                metadata,
                root / "out",
            )

            self.assertTrue(manifest["raw_contains_literal_space"])
            self.assertEqual(manifest["effective_token_count"], 2)

    def test_rejects_literal_space_when_use_space_char_would_append_another(self) -> None:
        decoded = ["A", " "]
        serialized = [json.dumps(token) for token in decoded]
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = self.make_lock(root, decoded, use_space_char=True)
            metadata = self.write_metadata(root, serialized)

            with self.assertRaisesRegex(subject.DictionaryExportError, "another space token"):
                subject.export_dictionary(
                    lock,
                    "pp-ocrv6-tiny-rec",
                    metadata,
                    root / "out",
                )

    def test_rejects_newline_token(self) -> None:
        decoded = ["A", "x\ny"]
        serialized = [json.dumps(token) for token in decoded]
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = self.make_lock(root, decoded)
            metadata = self.write_metadata(root, serialized)

            with self.assertRaisesRegex(subject.DictionaryExportError, "contains a newline"):
                subject.export_dictionary(
                    lock,
                    "pp-ocrv6-tiny-rec",
                    metadata,
                    root / "out",
                )

    def test_rejects_postprocess_drift(self) -> None:
        decoded = ["A"]
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = self.make_lock(root, decoded)
            metadata = self.write_metadata(root, ["A"], name="AttnLabelDecode")

            with self.assertRaisesRegex(subject.DictionaryExportError, "postprocess mismatch"):
                subject.export_dictionary(
                    lock,
                    "pp-ocrv6-tiny-rec",
                    metadata,
                    root / "out",
                )

    def test_rejects_source_artifact_not_locked_as_support(self) -> None:
        decoded = ["A"]
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock_payload = json.loads(
                self.make_lock(root, decoded).read_text(encoding="utf-8")
            )
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

    def test_rejects_raw_count_lock_drift(self) -> None:
        decoded = ["A", "B"]
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = self.make_lock(root, decoded, raw_token_count=3, effective_token_count=4)
            metadata = self.write_metadata(root, decoded)

            with self.assertRaisesRegex(subject.DictionaryExportError, "raw token count mismatch"):
                subject.export_dictionary(
                    lock, "pp-ocrv6-tiny-rec", metadata, root / "out"
                )

    def test_rejects_generated_digest_lock_drift(self) -> None:
        decoded = ["A"]
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = self.make_lock(root, decoded, generated_sha256="f" * 64)
            metadata = self.write_metadata(root, decoded)

            with self.assertRaisesRegex(subject.DictionaryExportError, "SHA-256 mismatch"):
                subject.export_dictionary(
                    lock, "pp-ocrv6-tiny-rec", metadata, root / "out"
                )


if __name__ == "__main__":
    unittest.main()
