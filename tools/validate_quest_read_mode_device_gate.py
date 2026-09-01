#!/usr/bin/env python3
"""Static anti-false-positive contract for the self-hosted Quest 3 Read Mode gate."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / ".github/workflows/quest3-read-mode-smoke.yml"
RUNNER = ROOT / "tools/run_quest_read_mode_smoke.py"
BUILD_SH = ROOT / "tools/unity/build-android-read-mode-fixture.sh"
BUILD_CS = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerReadModeFixtureAndroidBuild.cs"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def validate() -> dict[str, object]:
    workflow = WORKFLOW.read_text(encoding="utf-8")
    runner = RUNNER.read_text(encoding="utf-8")
    build_sh = BUILD_SH.read_text(encoding="utf-8")
    build_cs = BUILD_CS.read_text(encoding="utf-8")

    for fragment in (
        "workflow_dispatch:",
        "runs-on: [self-hosted, unity, unity-6000-0-66f2, quest3, adb]",
        'default: "Quest 3"',
        "python tools/stage_models.py --purpose-prefix ocr- --include-support",
        "python tools/prepare_unity_ocr_assets.py",
        "PHRASELAYER_JAPANESE_FONT_SOURCE:",
        "build-android-read-mode-fixture.sh",
        'assert data["surface_runtime"] == "MRUKEnvironmentRaycast"',
        'assert data["translation_runtime"] == "DemoDictionaryFixture"',
        'assert data["product_translation_gate"] is False',
        "python tools/run_quest_read_mode_smoke.py",
        'assert data["readiness"]["ocr_smoke_passed"] is True',
        'assert data["readiness"]["read_mode_smoke_passed"] is True',
        "if: always()",
        "phraselayer-quest3-read-mode-evidence",
    ):
        require(workflow, fragment, "Quest Read Mode workflow")

    for fragment in (
        'DEFAULT_PACKAGE = "com.unjuno.phraselayer.readmodefixture"',
        'DEFAULT_EXPECTED_DEVICE_MODEL = "Quest 3"',
        'OCR_PASS_MARKER = "PhraseLayer Quest OCR smoke test PASS"',
        'READ_MODE_PASS_MARKER = "PhraseLayer Quest Read Mode smoke test PASS"',
        'SURFACE_RUNTIME_MARKER = "surface_runtime=MRUKEnvironmentRaycast"',
        'FATAL_MARKER = "FATAL EXCEPTION"',
        "require_device_model(actual_device_model, args.expected_device_model)",
        '"logcat", "-c"',
        '"install", "-r", "-g"',
        '"surface_runtime": "MRUKEnvironmentRaycast"',
        '"translation_runtime": "DemoDictionaryFixture"',
        '"product_translation_gate": False',
        '"scope": (',
    ):
        require(runner, fragment, "Quest Read Mode device runner")

    for fragment in (
        "UNITY_EDITOR must point to the Unity 6000.0.66f2 Editor executable.",
        "PHRASELAYER_JAPANESE_FONT_SOURCE",
        "PHRASELAYER_READ_MODE_FIXTURE_APK_PATH",
        "PhraseLayerReadModeFixtureAndroidBuild.BuildBatch",
        "PhraseLayer.read-mode-fixture-build-evidence.json",
    ):
        require(build_sh, fragment, "Read Mode Android build shell")

    for fragment in (
        'DefaultApplicationIdentifier = "com.unjuno.phraselayer.readmodefixture"',
        '\\"ocr_runtime\\": \\"PaddleOCR\\"',
        '\\"surface_runtime\\": \\"MRUKEnvironmentRaycast\\"',
        '\\"translation_runtime\\": \\"DemoDictionaryFixture\\"',
        '\\"product_translation_gate\\": false',
        '\\"quest_read_mode_smoke_autorun\\": true',
        'PlayerSettings.SetScriptingBackend(namedTarget, ScriptingImplementation.IL2CPP)',
        'PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64',
    ):
        require(build_cs, fragment, "Read Mode fixture Android builder")

    return {
        "status": "pass",
        "self_hosted_quest3_runner_required": True,
        "actual_device_model_verified": True,
        "pinned_ocr_staged": True,
        "mruk_live_depth_surface_required": True,
        "reviewed_external_japanese_font_required": True,
        "android_arm64_il2cpp_required": True,
        "ocr_and_read_mode_pass_markers_required": True,
        "fatal_exception_rejected": True,
        "fixture_translation_not_product_gate": True,
        "evidence_uploaded_on_failure": True,
        "real_quest_execution_still_required": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
