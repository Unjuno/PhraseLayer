#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import pathlib
import unittest

SCRIPT = pathlib.Path(__file__).with_name("summarize_listen_mode_metrics.py")
SPEC = importlib.util.spec_from_file_location("summarize_listen_mode_metrics", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)


class ListenModeMetricsTests(unittest.TestCase):
    def test_parse_and_summarize_machine_readable_lines(self) -> None:
        lines = [
            "noise before",
            "I/Unity: PHRASELAYER_LISTEN_METRIC utterance=1 audio_s=2.000000 pipeline_ms=1000.000 processing_to_audio=0.500000 transcript_chars=12 adaptive_plan=1",
            "I/Unity: PHRASELAYER_LISTEN_METRIC utterance=2 audio_s=4.000000 pipeline_ms=1000.000 processing_to_audio=0.250000 transcript_chars=8 adaptive_plan=0",
            "I/Unity: PHRASELAYER_LISTEN_METRIC utterance=3 audio_s=1.000000 pipeline_ms=1500.000 processing_to_audio=1.500000 transcript_chars=5 adaptive_plan=1",
        ]
        samples = subject.parse_lines(lines)
        report = subject.summarize(samples)
        self.assertEqual(3, report["sample_count"])
        self.assertEqual(7.0, report["audio_seconds"]["total"])
        self.assertEqual(1000.0, report["pipeline_milliseconds"]["median"])
        self.assertEqual(1500.0, report["pipeline_milliseconds"]["p95_nearest_rank"])
        self.assertEqual(0.5, report["processing_to_audio_ratio"]["median"])
        self.assertEqual(1.5, report["processing_to_audio_ratio"]["maximum"])
        self.assertEqual(2, report["adaptive_plan_sample_count"])
        self.assertEqual(25, report["transcript_character_count"])

    def test_malformed_metric_line_fails_loudly(self) -> None:
        with self.assertRaisesRegex(subject.MetricsError, "malformed"):
            subject.parse_lines(["PHRASELAYER_LISTEN_METRIC utterance=nope"])

    def test_empty_sample_set_is_rejected(self) -> None:
        with self.assertRaisesRegex(subject.MetricsError, "no PHRASELAYER"):
            subject.summarize([])

    def test_duplicate_utterance_ids_are_rejected(self) -> None:
        samples = subject.parse_lines([
            "PHRASELAYER_LISTEN_METRIC utterance=1 audio_s=1.0 pipeline_ms=10.0 processing_to_audio=0.01 transcript_chars=1 adaptive_plan=0",
            "PHRASELAYER_LISTEN_METRIC utterance=1 audio_s=1.0 pipeline_ms=11.0 processing_to_audio=0.011 transcript_chars=1 adaptive_plan=0",
        ])
        with self.assertRaisesRegex(subject.MetricsError, "duplicate"):
            subject.summarize(samples)

    def test_nearest_rank_percentile(self) -> None:
        self.assertEqual(10.0, subject.percentile_nearest_rank([1.0, 2.0, 3.0, 4.0, 10.0], 95.0))
        self.assertEqual(3.0, subject.percentile_nearest_rank([1.0, 2.0, 3.0], 50.1))


if __name__ == "__main__":
    unittest.main()
