#!/usr/bin/env python3
"""Static regression gate for the real Quest 3 Listen Mode workflow.

This does not substitute for a Quest run. It only ensures the manual self-hosted workflow cannot silently regress
back to an ASR-only/dictionary build while still being named a Listen Mode smoke test.
"""

from __future__ import annotations

import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / ".github/workflows/quest3-listen-mode-smoke.yml"
SMOKE = ROOT / "tools/run_quest_listen_mode_smoke.py"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def validate() -> dict[str, object]:
    workflow = WORKFLOW.read_text(encoding="utf-8")
    smoke = SMOKE.read_text(encoding="utf-8")

    for fragment in (
        "workflow_dispatch:",
        "marian_source_dir:",
        "runs-on: [self-hosted, unity, unity-6000-0-66f2, quest3, adb]",
        "tools/requirements-marian-export.txt",
        "stage-marian-runtime.sh",
        "moonshine-ai/moonshine",
        "build-android-listen-mode.sh",
        'assert data["translation_runtime"] == "Marian"',
        'assert data["asr_runtime"] == "MoonshineV1"',
        'assert data["dictionary_fallback_allowed"] is False',
        "run_quest_listen_mode_smoke.py",
        "capture_quest_listen_mode_metrics.py",
    ):
        require(workflow, fragment, "Quest workflow")

    for fragment in (
        'MARIAN_READY_MARKER = "Marian offline translation ready:"',
        '"marian_translation_ready": MARIAN_READY_MARKER in logcat',
        '"offline_translation_runtime": "Marian"',
        '"offline_asr_runtime": "MoonshineV1"',
        "android.permission.RECORD_AUDIO",
    ):
        require(smoke, fragment, "Quest startup smoke")

    return {
        "status": "pass",
        "runner_labels": ["self-hosted", "unity", "unity-6000-0-66f2", "quest3", "adb"],
        "translation_runtime": "Marian",
        "asr_runtime": "MoonshineV1",
        "startup_requires_both_models": True,
        "execution_scope": "static-wiring-only-real-quest-run-still-required",
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
