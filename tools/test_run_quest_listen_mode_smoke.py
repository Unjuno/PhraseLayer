#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import pathlib
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).with_name("run_quest_listen_mode_smoke.py")
SPEC = importlib.util.spec_from_file_location("run_quest_listen_mode_smoke", SCRIPT)
assert SPEC and SPEC.loader
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)


class QuestListenModeSmokeTests(unittest.TestCase):
    def test_parse_adb_devices_keeps_only_authorized_devices(self) -> None:
        output = """List of devices attached\nquest-1 device product:hollywood model:Quest_3\nother offline\nnope unauthorized\n\n"""
        self.assertEqual(["quest-1"], subject.parse_adb_devices(output))

    def test_choose_serial_requires_disambiguation(self) -> None:
        with self.assertRaisesRegex(subject.SmokeError, "multiple adb devices"):
            subject.choose_serial(["a", "b"], None)
        self.assertEqual("b", subject.choose_serial(["a", "b"], "b"))

    def test_choose_serial_rejects_missing_requested_device(self) -> None:
        with self.assertRaisesRegex(subject.SmokeError, "not connected/authorized"):
            subject.choose_serial(["a"], "missing")

    def test_device_model_normalization_accepts_adb_separator_variants(self) -> None:
        subject.require_device_model("Quest 3", "Quest_3")
        subject.require_device_model("quest-3", "QUEST 3")

    def test_device_model_gate_rejects_non_quest3_device(self) -> None:
        with self.assertRaisesRegex(subject.SmokeError, "refusing to claim Quest device evidence"):
            subject.require_device_model("Pixel 9", "Quest 3")

    def test_device_model_gate_rejects_empty_expectation(self) -> None:
        with self.assertRaisesRegex(subject.SmokeError, "must not be empty"):
            subject.require_device_model("Quest 3", "  ")

    def test_record_audio_permission_parser(self) -> None:
        granted = "android.permission.RECORD_AUDIO: granted=true, flags=[ USER_SENSITIVE_WHEN_GRANTED ]"
        denied = "android.permission.RECORD_AUDIO: granted=false, flags=[ USER_SENSITIVE_WHEN_DENIED ]"
        self.assertTrue(subject.record_audio_granted(granted))
        self.assertFalse(subject.record_audio_granted(denied))

    def test_readiness_requires_microphone_marian_and_listen_runtime(self) -> None:
        log = (
            "I Unity: Marian offline translation ready: LocalTranslationAssets; cache=device-resident-experiment; source<=128; target<=128; beam=1.\n"
            "I Unity: Microphone capture started: Oculus Microphone; requested=48000 Hz; actual=48000 Hz\n"
            "I Unity: Listen Mode ready: microphone -> Moonshine ASR -> adaptive language plan.\n"
        )
        readiness = subject.readiness_from_logcat(log)
        self.assertTrue(readiness["microphone_started"])
        self.assertTrue(readiness["marian_translation_ready"])
        self.assertTrue(readiness["listen_mode_ready"])
        self.assertFalse(readiness["fatal_exception"])

    def test_missing_marian_marker_is_not_ready(self) -> None:
        log = (
            "I Unity: Microphone capture started: Oculus Microphone; requested=48000 Hz; actual=48000 Hz\n"
            "I Unity: Listen Mode ready: microphone -> Moonshine ASR -> adaptive language plan.\n"
        )
        readiness = subject.readiness_from_logcat(log)
        self.assertTrue(readiness["microphone_started"])
        self.assertFalse(readiness["marian_translation_ready"])
        self.assertTrue(readiness["listen_mode_ready"])

    def test_fatal_exception_is_detected(self) -> None:
        readiness = subject.readiness_from_logcat("E AndroidRuntime: FATAL EXCEPTION: main")
        self.assertTrue(readiness["fatal_exception"])

    def test_sha256_file_is_deterministic(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            path = pathlib.Path(temp) / "x.apk"
            path.write_bytes(b"PhraseLayer")
            self.assertEqual(
                "6b8af6df2f4e0266ba67ba934d21c4000e334abf52956913fc9c70005c9cae07",
                subject.sha256_file(path),
            )


if __name__ == "__main__":
    unittest.main()
