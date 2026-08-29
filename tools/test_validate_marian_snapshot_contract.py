#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import pathlib
import tempfile
import unittest

MODULE_PATH = pathlib.Path(__file__).with_name("validate_marian_snapshot_contract.py")
spec = importlib.util.spec_from_file_location("validate_marian_snapshot_contract", MODULE_PATH)
subject = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(subject)

REVISION = "a" * 40


def reviewed_config():
    return {
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


def reviewed_generation_config():
    return {
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


def reviewed_tokenizer_config():
    return {"source_lang": "en", "target_lang": "jap"}


def reviewed_vocabulary():
    vocabulary = {"</s>": 0, "<unk>": 1}
    for token_id in range(2, 46275):
        vocabulary[f"piece-{token_id}"] = token_id
    vocabulary["<pad>"] = 46275
    return vocabulary


def reviewed_readme():
    return "---\ntags:\n- translation\nlicense: apache-2.0\n---\n\n### opus-mt-en-jap\n"


def write_snapshot(
    root: pathlib.Path,
    *,
    config=None,
    generation=None,
    tokenizer=None,
    vocabulary=None,
    readme=None,
):
    root.mkdir(parents=True, exist_ok=True)
    (root / "README.md").write_text(readme if readme is not None else reviewed_readme(), encoding="utf-8")
    (root / "config.json").write_text(
        json.dumps(config if config is not None else reviewed_config()), encoding="utf-8"
    )
    (root / "generation_config.json").write_text(
        json.dumps(generation if generation is not None else reviewed_generation_config()), encoding="utf-8"
    )
    (root / "tokenizer_config.json").write_text(
        json.dumps(tokenizer if tokenizer is not None else reviewed_tokenizer_config()), encoding="utf-8"
    )
    (root / "vocab.json").write_text(
        json.dumps(vocabulary if vocabulary is not None else reviewed_vocabulary()), encoding="utf-8"
    )
    (root / "source.spm").write_bytes(b"synthetic-source-sentencepiece")
    (root / "target.spm").write_bytes(b"synthetic-target-sentencepiece")


class MarianSnapshotContractTests(unittest.TestCase):
    def test_reviewed_snapshot_passes_and_emits_hashed_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            write_snapshot(root)
            manifest = subject.validate_snapshot(root, REVISION)

        self.assertEqual("Helsinki-NLP/opus-mt-en-jap", manifest["model_id"])
        self.assertEqual(REVISION, manifest["revision"])
        self.assertEqual("apache-2.0", manifest["license"])
        self.assertEqual({"source": "en", "target": "jap"}, manifest["languages"])
        self.assertEqual([46275], manifest["generation_policy"]["bad_word_token_ids"])
        self.assertEqual(7, len(manifest["artifacts"]))
        for artifact in manifest["artifacts"]:
            self.assertEqual(64, len(artifact["sha256"]))
            self.assertGreater(artifact["size_bytes"], 0)

    def test_short_or_uppercase_revision_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            write_snapshot(root)
            with self.assertRaisesRegex(subject.SnapshotContractError, "full lowercase 40-character"):
                subject.validate_snapshot(root, "a863894")
            with self.assertRaisesRegex(subject.SnapshotContractError, "full lowercase 40-character"):
                subject.validate_snapshot(root, "A" * 40)

    def test_license_drift_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            write_snapshot(root, readme="---\nlicense: mit\n---\n")
            with self.assertRaisesRegex(subject.SnapshotContractError, "license expected"):
                subject.validate_snapshot(root, REVISION)

    def test_missing_or_duplicate_license_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            write_snapshot(root, readme="---\ntags:\n- translation\n---\n")
            with self.assertRaisesRegex(subject.SnapshotContractError, "exactly one license"):
                subject.validate_snapshot(root, REVISION)

        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            write_snapshot(root, readme="---\nlicense: apache-2.0\nlicense: apache-2.0\n---\n")
            with self.assertRaisesRegex(subject.SnapshotContractError, "exactly one license"):
                subject.validate_snapshot(root, REVISION)

    def test_language_direction_drift_is_rejected(self):
        tokenizer = reviewed_tokenizer_config()
        tokenizer["target_lang"] = "ja"
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            write_snapshot(root, tokenizer=tokenizer)
            with self.assertRaisesRegex(subject.SnapshotContractError, "target_lang"):
                subject.validate_snapshot(root, REVISION)

    def test_generation_pad_ban_drift_is_rejected(self):
        generation = reviewed_generation_config()
        generation["bad_words_ids"] = []
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            write_snapshot(root, generation=generation)
            with self.assertRaisesRegex(subject.SnapshotContractError, "bad_words_ids"):
                subject.validate_snapshot(root, REVISION)

    def test_model_geometry_drift_is_rejected(self):
        config = reviewed_config()
        config["decoder_attention_heads"] = 16
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            write_snapshot(root, config=config)
            with self.assertRaisesRegex(subject.SnapshotContractError, "decoder_attention_heads"):
                subject.validate_snapshot(root, REVISION)

    def test_duplicate_vocabulary_id_is_rejected(self):
        vocabulary = reviewed_vocabulary()
        vocabulary["piece-100"] = 101
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            write_snapshot(root, vocabulary=vocabulary)
            with self.assertRaisesRegex(subject.SnapshotContractError, "unique"):
                subject.validate_snapshot(root, REVISION)

    def test_missing_sentencepiece_artifact_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            write_snapshot(root)
            (root / "target.spm").unlink()
            with self.assertRaisesRegex(subject.SnapshotContractError, "target.spm"):
                subject.validate_snapshot(root, REVISION)


if __name__ == "__main__":
    unittest.main()
