#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import pathlib
import tempfile
import types
import unittest

SCRIPT = pathlib.Path(__file__).with_name("fetch_moonshine_weight_metadata.py")
SPEC = importlib.util.spec_from_file_location("fetch_moonshine_weight_metadata", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)

REVISION = subject.PINNED_REVISION
SHA = "8" * 64


class MoonshineWeightMetadataTests(unittest.TestCase):
    def test_fetches_exact_pinned_weight_identity_without_download(self) -> None:
        calls = []

        def build_url(**kwargs):
            calls.append(kwargs)
            return "https://example.invalid/model.safetensors"

        def get_metadata(url):
            self.assertEqual("https://example.invalid/model.safetensors", url)
            return types.SimpleNamespace(commit_hash=REVISION, size=108389192, etag='"' + SHA + '"')

        result = subject.fetch_weight_metadata(
            REVISION,
            build_url=build_url,
            get_metadata=get_metadata,
        )

        self.assertEqual([{
            "repo_id": subject.MODEL_ID,
            "filename": subject.WEIGHT_FILENAME,
            "revision": REVISION,
        }], calls)
        self.assertEqual(108389192, result["artifact"]["size_bytes"])
        self.assertEqual(SHA, result["artifact"]["sha256"])
        self.assertFalse(result["weight_downloaded"])
        self.assertTrue(result["local_file_hash_required_before_export"])

    def test_accepts_weak_quoted_sha_etag(self) -> None:
        metadata = types.SimpleNamespace(commit_hash=REVISION, size=1, etag='W/"' + SHA + '"')
        result = subject.fetch_weight_metadata(
            REVISION,
            build_url=lambda **kwargs: "https://example.invalid/file",
            get_metadata=lambda url: metadata,
        )
        self.assertEqual(SHA, result["artifact"]["sha256"])

    def test_rejects_short_or_unreviewed_revision(self) -> None:
        with self.assertRaisesRegex(subject.WeightMetadataError, "full lowercase 40-character"):
            subject.fetch_weight_metadata(
                "390624e",
                build_url=lambda **kwargs: "",
                get_metadata=lambda url: None,
            )
        with self.assertRaisesRegex(subject.WeightMetadataError, "revision drift"):
            subject.fetch_weight_metadata(
                "a" * 40,
                build_url=lambda **kwargs: "",
                get_metadata=lambda url: None,
            )

    def test_rejects_commit_drift_invalid_size_or_non_sha_etag(self) -> None:
        with self.assertRaisesRegex(subject.WeightMetadataError, "commit drift"):
            subject.fetch_weight_metadata(
                REVISION,
                build_url=lambda **kwargs: "url",
                get_metadata=lambda url: types.SimpleNamespace(commit_hash="a" * 40, size=1, etag=SHA),
            )
        with self.assertRaisesRegex(subject.WeightMetadataError, "positive byte size"):
            subject.fetch_weight_metadata(
                REVISION,
                build_url=lambda **kwargs: "url",
                get_metadata=lambda url: types.SimpleNamespace(commit_hash=REVISION, size=0, etag=SHA),
            )
        with self.assertRaisesRegex(subject.WeightMetadataError, "content SHA256"):
            subject.fetch_weight_metadata(
                REVISION,
                build_url=lambda **kwargs: "url",
                get_metadata=lambda url: types.SimpleNamespace(commit_hash=REVISION, size=1, etag="not-a-sha"),
            )

    def test_writes_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as raw:
            path = pathlib.Path(raw) / "manifest.json"
            subject.write_manifest(path, {"ok": True})
            self.assertEqual('{\n  "ok": true\n}\n', path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
