#!/usr/bin/env python3
from __future__ import annotations

import copy
import importlib.util
import json
import pathlib
import unittest

SCRIPT = pathlib.Path(__file__).with_name("validate_moonshine_v1_reference_trace.py")
SPEC = importlib.util.spec_from_file_location("validate_moonshine_v1_reference_trace", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)


def load_json(path: pathlib.Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


class MoonshineReferenceTraceEvidenceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.reference = load_json(subject.DEFAULT_REFERENCE)
        self.graph = load_json(subject.DEFAULT_GRAPH_EVIDENCE)

    def test_committed_reference_validates(self) -> None:
        report = subject.validate_reference(self.reference, self.graph)
        self.assertEqual("validated", report["status"])
        self.assertEqual(16, report["emitted_token_count"])
        self.assertEqual(17, report["decoder_steps"])
        self.assertGreater(report["minimum_argmax_margin"], 0.0)
        self.assertFalse(report["actual_trace_compared"])

    def test_eos_in_emitted_tokens_is_rejected(self) -> None:
        reference = copy.deepcopy(self.reference)
        reference["generation"]["token_ids"][0] = subject.EXPECTED_EOS
        reference["generation"]["steps"][0]["selected_token_id"] = subject.EXPECTED_EOS
        with self.assertRaisesRegex(subject.ReferenceEvidenceError, "must not include EOS"):
            subject.validate_reference(reference, self.graph)

    def test_non_positive_argmax_margin_is_rejected(self) -> None:
        reference = copy.deepcopy(self.reference)
        step = reference["generation"]["steps"][2]
        step["runner_up_logit"] = step["selected_logit"]
        step["argmax_margin"] = 0.0
        reference["generation"]["minimum_argmax_margin"] = 0.0
        with self.assertRaisesRegex(subject.ReferenceEvidenceError, "must be positive"):
            subject.validate_reference(reference, self.graph)

    def test_graph_revision_mismatch_is_rejected(self) -> None:
        graph = copy.deepcopy(self.graph)
        graph["revision"] = "0" * 40
        with self.assertRaisesRegex(subject.ReferenceEvidenceError, "reference/graph revision mismatch"):
            subject.validate_reference(self.reference, graph)

    def test_actual_trace_exact_token_drift_is_rejected(self) -> None:
        generation = self.reference["generation"]
        fixture = self.reference["fixture"]
        runtime = self.reference["reference_runtime"]
        deployment = self.reference["deployment"]
        actual = {
            "schema_version": 1,
            "audio_size_bytes": fixture["size_bytes"],
            "audio_sha256": fixture["sha256"],
            "binding": deployment["binding"],
            "provider": runtime["provider"],
            "token_ids": list(generation["token_ids"]),
            "terminated_by_eos": True,
            "decoder_steps": generation["decoder_steps"],
            "transcript": generation["transcript"],
        }
        actual["token_ids"][2] += 1
        with self.assertRaisesRegex(subject.ReferenceEvidenceError, "token sequence"):
            subject.validate_reference(self.reference, self.graph, actual)

    def test_actual_trace_exact_match_passes(self) -> None:
        generation = self.reference["generation"]
        fixture = self.reference["fixture"]
        runtime = self.reference["reference_runtime"]
        deployment = self.reference["deployment"]
        actual = {
            "schema_version": 1,
            "audio_size_bytes": fixture["size_bytes"],
            "audio_sha256": fixture["sha256"],
            "binding": deployment["binding"],
            "provider": runtime["provider"],
            "token_ids": list(generation["token_ids"]),
            "terminated_by_eos": True,
            "decoder_steps": generation["decoder_steps"],
            "transcript": generation["transcript"],
        }
        report = subject.validate_reference(self.reference, self.graph, actual)
        self.assertTrue(report["actual_trace_compared"])


if __name__ == "__main__":
    unittest.main()
