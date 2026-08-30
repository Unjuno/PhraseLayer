#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import pathlib
import tempfile
import types
import unittest

SCRIPT = pathlib.Path(__file__).with_name("fetch_moonshine_snapshot_metadata.py")
SPEC = importlib.util.spec_from_file_location("fetch_moonshine_snapshot_metadata", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)

REVISION = subject.PINNED_REVISION


class MoonshineSnapshotFetchTests(unittest.TestCase):
    def test_resolve_requires_exact_pinned_full_revision(self) -> None:
        info = lambda *args, **kwargs: types.SimpleNamespace(sha=REVISION)
        self.assertEqual(REVISION, subject.resolve_revision(REVISION, model_info=info))

        wrong = lambda *args, **kwargs: types.SimpleNamespace(sha="a" * 40)
        with self.assertRaisesRegex(subject.SnapshotFetchError, "revision drift"):
            subject.resolve_revision(REVISION, model_info=wrong)

    def test_stage_requests_only_reviewed_small_artifacts(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            cache = pathlib.Path(raw) / "cache"
            destination = pathlib.Path(raw) / "stage"
            self._write_fixture(cache)
            calls = []

            def download_file(**kwargs):
                calls.append(kwargs["filename"])
                return str(cache / kwargs["filename"])

            manifest = subject.stage_small_snapshot(
                destination,
                REVISION,
                download_file=download_file,
            )

            self.assertEqual(list(subject.SMALL_ARTIFACTS), calls)
            self.assertEqual(list(subject.SMALL_ARTIFACTS), manifest["staging"]["allow_list"])
            self.assertEqual(32000, manifest["tokenizer_contract"]["base_vocabulary_size"])
            self.assertEqual(768, manifest["tokenizer_contract"]["added_token_entries"])
            self.assertFalse(manifest["staging"]["weights_downloaded"])
            self.assertFalse((destination / "model.safetensors").exists())

    def test_stage_rejects_unreviewed_revision(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            with self.assertRaisesRegex(subject.SnapshotFetchError, "pinned"):
                subject.stage_small_snapshot(pathlib.Path(raw), "a" * 40, download_file=lambda **kwargs: "")

    def test_stage_rejects_accidental_weight(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            cache = pathlib.Path(raw) / "cache"
            destination = pathlib.Path(raw) / "stage"
            self._write_fixture(cache)
            destination.mkdir(parents=True)
            (destination / "model.safetensors").write_bytes(b"do-not-stage")

            def download_file(**kwargs):
                return str(cache / kwargs["filename"])

            with self.assertRaisesRegex(subject.SnapshotFetchError, "weights/graphs"):
                subject.stage_small_snapshot(destination, REVISION, download_file=download_file)

    @staticmethod
    def _write_fixture(root: pathlib.Path) -> None:
        root.mkdir(parents=True)
        (root / "README.md").write_text("---\nlanguage: en\nlicense: mit\n---\n", encoding="utf-8")
        MoonshineSnapshotFetchTests._write_json(root / "config.json", {
            "architectures": ["MoonshineForConditionalGeneration"],
            "model_type": "moonshine",
            "is_encoder_decoder": True,
            "use_cache": True,
            "vocab_size": 32768,
            "hidden_size": 288,
            "encoder_num_hidden_layers": 6,
            "decoder_num_hidden_layers": 6,
            "encoder_num_attention_heads": 8,
            "decoder_num_attention_heads": 8,
            "max_position_embeddings": 194,
            "bos_token_id": 1,
            "decoder_start_token_id": 1,
            "eos_token_id": 2,
            "pad_token_id": 2,
        })
        MoonshineSnapshotFetchTests._write_json(root / "generation_config.json", {
            "bos_token_id": 1,
            "decoder_start_token_id": 1,
            "eos_token_id": 2,
            "pad_token_id": 2,
            "max_length": 194,
        })
        MoonshineSnapshotFetchTests._write_json(root / "preprocessor_config.json", {
            "feature_extractor_type": "Wav2Vec2FeatureExtractor",
            "feature_size": 1,
            "sampling_rate": 16000,
            "do_normalize": False,
            "return_attention_mask": True,
            "padding_value": 0.0,
        })
        MoonshineSnapshotFetchTests._write_json(root / "tokenizer.json", {
            "model": {"vocab": list(range(32000))},
            "added_tokens": [
                {"id": token_id, "content": f"<extra-{token_id}>"}
                for token_id in range(32000, 32768)
            ],
        })

    @staticmethod
    def _write_json(path: pathlib.Path, value) -> None:
        path.write_text(json.dumps(value), encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
