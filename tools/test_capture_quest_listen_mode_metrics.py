#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import pathlib
import unittest

SCRIPT = pathlib.Path(__file__).with_name("capture_quest_listen_mode_metrics.py")
SPEC = importlib.util.spec_from_file_location("capture_quest_listen_mode_metrics", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)


class QuestCaptureTests(unittest.TestCase):
    def test_parse_adb_devices_keeps_only_authorized_devices(self) -> None:
        output = """List of devices attached
ABC123 device product:foo model:Quest_3 device:eureka transport_id:1
XYZ999 unauthorized usb:1-2 transport_id:2
EMU555 offline transport_id:3
"""
        self.assertEqual(["ABC123"], subject.parse_adb_devices(output))

    def test_choose_serial_requires_disambiguation(self) -> None:
        self.assertEqual("ABC", subject.choose_serial(["ABC"], None))
        self.assertEqual("XYZ", subject.choose_serial(["ABC", "XYZ"], "XYZ"))
        with self.assertRaisesRegex(subject.CaptureError, "multiple"):
            subject.choose_serial(["ABC", "XYZ"], None)
        with self.assertRaisesRegex(subject.CaptureError, "not connected"):
            subject.choose_serial(["ABC"], "XYZ")
        with self.assertRaisesRegex(subject.CaptureError, "no authorized"):
            subject.choose_serial([], None)

    def test_metric_lines_filters_logcat_without_reformatting(self) -> None:
        lines = [
            "noise",
            "09-01 I/Unity: PHRASELAYER_LISTEN_METRIC utterance=1 audio_s=1.0 pipeline_ms=1.0 processing_to_audio=0.001 transcript_chars=2 adaptive_plan=0",
            "other",
        ]
        self.assertEqual([lines[1]], subject.metric_lines(lines))


if __name__ == "__main__":
    unittest.main()
