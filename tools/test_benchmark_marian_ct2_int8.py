#!/usr/bin/env python3
"""Dependency-free policy tests. Never stages or downloads real model weights."""
import json
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import benchmark_marian_ct2_int8 as audit


class LightweightMarianTests(unittest.TestCase):
    def test_candidate_matches_pinned_model_but_is_not_production(self):
        source, candidate = audit.load_policy_and_lock()
        self.assertEqual("opus-mt-en-jap", source["id"])
        self.assertEqual(source["revision"], candidate["revision"])
        self.assertFalse(candidate["bundled"])
        self.assertFalse(candidate["production_enabled"])
        self.assertEqual("int8_float32", candidate["quantization"])

    def test_rejects_missing_snapshot_before_loading_optional_ml_deps(self):
        source, _ = audit.load_policy_and_lock()
        with self.assertRaisesRegex(audit.CandidateRejected, "directory not found"):
            audit.verify_local_snapshot(pathlib.Path("/missing-locked-marian"), source)

    def test_rejects_tampered_weight_even_when_size_matches(self):
        source, _ = audit.load_policy_and_lock()
        # Need not create a 274MB file: a tiny substitution fails the locked size.
        with tempfile.TemporaryDirectory() as temp:
            p = pathlib.Path(temp)
            (p / "pytorch_model.bin").write_bytes(b"not-the-locked-weights")
            with self.assertRaises(audit.CandidateRejected):
                audit.verify_local_snapshot(p, source)

    def test_compact_agreement_can_qualify_for_device_trial_only(self):
        base = ["禁止", "非常口"] + ["日本語"] * 22
        report = audit.evaluate_outputs(base, list(base), 1000, 400)
        self.assertTrue(report["eligible_for_device_trial"])
        self.assertFalse(report["production_model_replaced"])
        self.assertFalse(report["quest_latency_measured"])
        self.assertFalse(report["translation_accuracy_measured"])

    def test_size_regression_is_rejected(self):
        a = ["日本語"] * 24
        self.assertFalse(audit.evaluate_outputs(a, a, 1000, 900)["eligible_for_device_trial"])

    def test_critical_output_change_blocks_promotion(self):
        a = ["日本語"] * 24
        b = list(a)
        b[0] = "別の訳"
        report = audit.evaluate_outputs(a, b, 1000, 400)
        self.assertFalse(report["eligible_for_device_trial"])
        self.assertEqual(23, report["exact_agreement_cases"])

    def test_blank_or_non_japanese_critical_rejected(self):
        a = ["日本語"] * 24
        b = list(a)
        b[0] = ""
        self.assertFalse(audit.evaluate_outputs(a, b, 1000, 400)["eligible_for_device_trial"])
        a[0] = b[0] = "KEEP OFF"
        self.assertFalse(audit.evaluate_outputs(a, b, 1000, 400)["eligible_for_device_trial"])

    def test_candidate_must_preserve_all_critical_indices(self):
        a = ["日本語"] * 2
        with self.assertRaisesRegex(audit.CandidateRejected, "length"):
            audit.evaluate_outputs(a, ["日本語"], 100, 20)
        report = audit.evaluate_outputs(a, a, 100, 20)
        self.assertFalse(report["eligible_for_device_trial"])

    def test_invalid_quantities_and_types_rejected(self):
        with self.assertRaises(audit.CandidateRejected):
            audit.evaluate_outputs(["日本語"], ["日本語"], 0, 10)
        with self.assertRaises(audit.CandidateRejected):
            audit.evaluate_outputs(["日本語"], [None], 100, 10)
        with self.assertRaises(audit.CandidateRejected):
            audit.quantile([], 0.95)

    def test_report_does_not_expose_source_text_or_model_paths(self):
        # The pure summary contains no text field, input location or translated strings.
        m = audit.evaluate_outputs(["日本語"] * 24, ["日本語"] * 24, 1000, 400)
        rendered = json.dumps(m, ensure_ascii=False)
        self.assertNotIn("日本語", rendered)
        self.assertNotIn("keep off", rendered)
        self.assertNotIn("snapshot", rendered.lower())


if __name__ == "__main__":
    unittest.main()
