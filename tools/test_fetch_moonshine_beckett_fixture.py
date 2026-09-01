#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import importlib.util
import pathlib
import unittest

SCRIPT = pathlib.Path(__file__).with_name("fetch_moonshine_beckett_fixture.py")
SPEC = importlib.util.spec_from_file_location("fetch_moonshine_beckett_fixture", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)


class MoonshineBeckettFixtureTests(unittest.TestCase):
    def test_git_blob_sha_matches_git_object_encoding(self) -> None:
        data = b"hello\n"
        expected = hashlib.sha1(b"blob 6\0hello\n").hexdigest()
        self.assertEqual(expected, subject.git_blob_sha1(data))

    def test_validate_fixture_checks_size_blob_and_wave_signature(self) -> None:
        data = b"RIFF" + b"\x00" * 4 + b"WAVE" + b"fixture"
        original_size = subject.EXPECTED_SIZE_BYTES
        original_blob = subject.EXPECTED_GIT_BLOB_SHA1
        try:
            subject.EXPECTED_SIZE_BYTES = len(data)
            subject.EXPECTED_GIT_BLOB_SHA1 = subject.git_blob_sha1(data)
            report = subject.validate_fixture_bytes(data)
            self.assertEqual(len(data), report["size_bytes"])
            self.assertEqual(64, len(report["sha256"]))
        finally:
            subject.EXPECTED_SIZE_BYTES = original_size
            subject.EXPECTED_GIT_BLOB_SHA1 = original_blob

    def test_validate_fixture_rejects_identity_drift(self) -> None:
        with self.assertRaisesRegex(subject.FixtureError, "size drift"):
            subject.validate_fixture_bytes(b"RIFF")

    def test_raw_url_is_commit_pinned(self) -> None:
        url = subject.raw_url()
        self.assertIn(subject.UPSTREAM_REVISION, url)
        self.assertNotIn("/main/", url)
        self.assertTrue(url.endswith("/" + subject.UPSTREAM_PATH))


if __name__ == "__main__":
    unittest.main()
