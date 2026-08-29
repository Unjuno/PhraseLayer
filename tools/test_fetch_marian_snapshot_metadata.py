#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import pathlib
import tempfile
import types
import unittest

MODULE_PATH = pathlib.Path(__file__).with_name("fetch_marian_snapshot_metadata.py")
spec = importlib.util.spec_from_file_location("fetch_marian_snapshot_metadata", MODULE_PATH)
subject = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(subject)

REVISION = "0123456789abcdef0123456789abcdef01234567"


def valid_snapshot_files():
    config = {
        "architectures": ["MarianMTModel"],
        "model_type": "marian",
        "is_encoder_decoder": True,
        "vocab_size": 46276,
        "decoder_vocab_size": 46276,
        "d_model": 512,
        "encoder_layers": 6,
        "decoder_layers": 6,
        "encoder_attention_heads": 8,
        "decoder_attention_heads": 8,
        "max_position_embeddings": 512,
        "max_length": 512,
        "bad_words_ids": [[46275]],
        "bos_token_id": 0,
        "eos_token_id": 0,
        "forced_eos_token_id": 0,
        "pad_token_id": 46275,
        "decoder_start_token_id": 46275,
        "num_beams": 4,
        "use_cache": True,
    }
    generation = {
        "bad_words_ids": [[46275]],
        "bos_token_id": 0,
        "decoder_start_token_id": 46275,
        "eos_token_id": 0,
        "forced_eos_token_id": 0,
        "max_length": 512,
        "num_beams": 4,
        "pad_token_id": 46275,
        "renormalize_logits": True,
    }
    tokenizer = {"source_lang": "en", "target_lang": "jap"}
    vocab = {"</s>": 0, "<unk>": 1}
    for token_id in range(2, 46275):
        vocab[f"piece-{token_id}"] = token_id
    vocab["<pad>"] = 46275
    return {
        "README.md": b"---\ntags:\n- translation\nlicense: apache-2.0\n---\n",
        "config.json": json.dumps(config).encode(),
        "generation_config.json": json.dumps(generation).encode(),
        "tokenizer_config.json": json.dumps(tokenizer).encode(),
        "source.spm": b"source-model",
        "target.spm": b"target-model",
        "vocab.json": json.dumps(vocab).encode(),
    }


class MarianSnapshotFetchTests(unittest.TestCase):
    def test_revision_resolves_to_full_sha_and_enforces_prefix(self):
        calls = []

        def fake_model_info(repo_id, revision):
            calls.append((repo_id, revision))
            return types.SimpleNamespace(sha=REVISION)

        resolved = subject.resolve_revision(
            "main",
            expected_prefix="0123456",
            model_info=fake_model_info,
        )
        self.assertEqual(REVISION, resolved)
        self.assertEqual([(subject.MODEL_ID, "main")], calls)

    def test_revision_prefix_mismatch_fails(self):
        def fake_model_info(repo_id, revision):
            return types.SimpleNamespace(sha=REVISION)

        with self.assertRaisesRegex(subject.SnapshotFetchError, "does not start"):
            subject.resolve_revision("main", expected_prefix="abcdef0", model_info=fake_model_info)

    def test_short_resolved_sha_is_rejected(self):
        def fake_model_info(repo_id, revision):
            return types.SimpleNamespace(sha="a863894")

        with self.assertRaisesRegex(subject.SnapshotFetchError, "full lowercase 40-character"):
            subject.resolve_revision("main", model_info=fake_model_info)

    def test_small_snapshot_stages_only_explicit_allow_list_and_validates(self):
        files = valid_snapshot_files()
        calls = []
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)

            def fake_download(**kwargs):
                calls.append(kwargs)
                target = pathlib.Path(kwargs["local_dir"]) / kwargs["filename"]
                target.write_bytes(files[kwargs["filename"]])
                return str(target)

            manifest = subject.stage_small_snapshot(root, REVISION, download_file=fake_download)

            self.assertEqual(list(subject.SMALL_ARTIFACTS), [call["filename"] for call in calls])
            self.assertTrue(all(call["revision"] == REVISION for call in calls))
            self.assertFalse((root / "pytorch_model.bin").exists())
            self.assertFalse(manifest["staging"]["weights_downloaded"])
            self.assertEqual("apache-2.0", manifest["license"])
            self.assertEqual(7, len(manifest["artifacts"]))

    def test_existing_weight_in_destination_is_rejected(self):
        files = valid_snapshot_files()
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            (root / "pytorch_model.bin").write_bytes(b"must-not-be-here")

            def fake_download(**kwargs):
                target = pathlib.Path(kwargs["local_dir"]) / kwargs["filename"]
                target.write_bytes(files[kwargs["filename"]])
                return str(target)

            with self.assertRaisesRegex(subject.SnapshotFetchError, "must not stage"):
                subject.stage_small_snapshot(root, REVISION, download_file=fake_download)

    def test_download_adapter_must_materialize_artifact(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)

            def fake_download(**kwargs):
                return str(root / "missing-cache-entry")

            with self.assertRaisesRegex(subject.SnapshotFetchError, "did not produce"):
                subject.stage_small_snapshot(root, REVISION, download_file=fake_download)


if __name__ == "__main__":
    unittest.main()
