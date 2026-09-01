#!/usr/bin/env python3
"""Static contract for the non-Quest ARM64 Android Marian product-translation runtime gate."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / ".github/workflows/marian-android-runtime-smoke.yml"
RUNNER = ROOT / "tools/run_marian_android_runtime_smoke.py"
SMOKE_BEHAVIOUR = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/MarianAndroidRuntimeSmokeTestBehaviour.cs"
DEMO_BEHAVIOUR = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/PhraseLayerDemoBehaviour.cs"
SCENE_SETUP = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerMarianProductFixtureSetup.cs"
BUILD = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerMarianProductAndroidBuild.cs"
GUARDED_CSPROJ = ROOT / "tests/PhraseLayer.UnityMarianInferenceShell.Compile/PhraseLayer.UnityMarianInferenceShell.Compile.csproj"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def forbid(text: str, fragment: str, label: str) -> None:
    if fragment in text:
        raise GateError(f"{label} contains forbidden marker: {fragment}")


def validate() -> dict[str, object]:
    workflow = WORKFLOW.read_text(encoding="utf-8")
    runner = RUNNER.read_text(encoding="utf-8")
    smoke = SMOKE_BEHAVIOUR.read_text(encoding="utf-8")
    demo = DEMO_BEHAVIOUR.read_text(encoding="utf-8")
    scene = SCENE_SETUP.read_text(encoding="utf-8")
    build = BUILD.read_text(encoding="utf-8")
    guarded = GUARDED_CSPROJ.read_text(encoding="utf-8")

    for fragment in (
        'FixtureSource = "keep off"',
        'ReferenceResourcePath = "LocalTranslationAssets/marian-reference"',
        "InitializeBootstrap()",
        "bootstrap.Initialize()",
        "bootstrapReady = bootstrap.IsSupported && bootstrap.IsReady",
        "translationOverride = demo.UsesTranslationEngineOverride",
        "demo.PrepareDeterministicTranslationSmokeFixture(FixtureSource, 0.0)",
        "await demo.ReplanAsync()",
        "plan.Assistance.Decisions.Count != 1",
        "plan.Segments.Count != 1 || !plan.Segments[0].IsAssisted",
        "plan.Segments[0].SourceText, FixtureSource",
        "plan.DisplayText, expectedTranslation",
        'BuildReport(\n                    "PASS"',
        'BuildReport(\n                    "FAIL_EXCEPTION"',
        'Append(" bootstrap_ready=").Append(bootstrapReady ? "true" : "false")',
        'Append(" translation_override=").Append(translationOverride ? "true" : "false")',
        'builder.AppendLine("PhraseLayer Marian Android runtime smoke " + status)',
        "reference_match=",
        "UnityMarianDeviceResidentGenerationBackend",
        "semantic_span_pipeline=true product_translation_gate=true",
        "translated_text=<redacted; exact offline reference match required>",
        "failure_type=",
    ):
        require(smoke, fragment, "Marian Android runtime smoke behaviour")

    for fragment in (
        "public void PrepareDeterministicTranslationSmokeFixture(string text, double understanding)",
        "translationEngineOverride == null",
        "assistanceMode = AssistanceMode.Balanced",
        "BuildPipeline()",
        "learner.SetUnderstanding(text, understanding)",
        "currentPlan = null",
        "currentEncounter = null",
        "demo dictionary fallback is not allowed",
    ):
        require(demo, fragment, "PhraseLayer deterministic Marian translation smoke fixture")

    for fragment in (
        "demo.SetAutoRunOnStart(false)",
        "root.AddComponent<MarianAndroidRuntimeSmokeTestBehaviour>()",
        "runtimeSmoke.SetSceneReferences(demo, bootstrap)",
        "runtimeSmoke.SetAutoRun(true)",
        "Android runtime smoke autorun",
    ):
        require(scene, fragment, "Marian product fixture scene")

    for fragment in (
        '\\"android_runtime_smoke_autorun\\": true',
        '\\"android_runtime_smoke_fixture_source\\": \\"keep off\\"',
        '\\"android_runtime_smoke_reference_resource\\": \\"LocalTranslationAssets/marian-reference\\"',
        '\\"android_runtime_smoke_exact_reference_match_required\\": true',
        '\\"android_runtime_execution_performed\\": false',
        "runtime_smoke_autorun=true; runtime not executed by packaging",
    ):
        require(build, fragment, "Marian Android build evidence")

    require(guarded, "MarianAndroidRuntimeSmokeTestBehaviour.cs", "guarded Marian compile project")

    for fragment in (
        'DEFAULT_PACKAGE = "com.unjuno.phraselayer.marianfixture"',
        'PASS_MARKER = "PhraseLayer Marian Android runtime smoke PASS"',
        'REFERENCE_MARKER = "reference_match=true"',
        'PRODUCT_GATE_MARKER = "product_translation_gate=true"',
        'BACKEND_MARKER = "generation_backend=UnityMarianDeviceResidentGenerationBackend"',
        "require_arm64_abi",
        '"arm64-v8a" not in abis',
        "SAFE_DIAGNOSTIC_PATTERNS = tuple(",
        "pattern.fullmatch(candidate)",
        "redact_failure_message",
        '"android_runtime_execution_performed": True',
        '"quest_device_execution_performed": False',
        '"network_required": False',
        '"raw_process_logcat_written_to_disk": False',
        '"raw_process_logcat_uploaded": False',
        '"translated_text_allowed_in_diagnostics": False',
        "--uninstall-after",
        "diagnostics_path.write_text(sanitize_logcat_diagnostics(logcat)",
    ):
        require(runner, fragment, "Marian Android adb runner")
    for forbidden in (
        '"adb_serial": serial',
        "logcat.txt",
        "quest3",
        "android.permission.CAMERA",
        "MRUK",
    ):
        forbid(runner, forbidden, "Marian Android adb runner")

    for fragment in (
        "workflow_dispatch:",
        "runs-on: [self-hosted, unity, unity-6000-0-66f2, adb]",
        "marian_source_snapshot:",
        "marian_onnx_dir:",
        "Run real Unity host capability preflight",
        "Require real Unity exact-token Marian translation parity",
        "Build local-only Marian Android ARM64 IL2CPP runtime fixture",
        "android_runtime_smoke_autorun",
        "run_marian_android_runtime_smoke.py",
        "--uninstall-after",
        'smoke["status"]=="pass"',
        'smoke["readiness"]["exact_reference_match_observed"] is True',
        'smoke["product_translation_gate"] is True',
        'smoke["android_runtime_execution_performed"] is True',
        'smoke["quest_device_execution_performed"] is False',
        'smoke["network_required"] is False',
        'structure["native_abis"]==["arm64-v8a"]',
        '"purpose":"phrase-layer-marian-android-runtime-product-gate"',
        '"exact_offline_reference_match":True',
        '"apk_uploaded":False',
        '"model_weights_uploaded":False',
        "Remove local Marian APK before artifact phase",
        'rm -f "$RUNNER_TEMP/PhraseLayerMarianProductFixture.apk"',
        "Upload safe Marian Android runtime evidence only",
        "phraselayer-marian-android-runtime-evidence",
    ):
        require(workflow.replace(" ", ""), fragment.replace(" ", ""), "Marian Android runtime workflow")

    upload = workflow.split("- name: Upload safe Marian Android runtime evidence only", 1)[1]
    for forbidden in (
        "PhraseLayerMarianProductFixture.apk",
        "encoder_model.onnx",
        "decoder_model.onnx",
        "decoder_with_past_model.onnx",
        "pytorch_model.bin",
        "**",
    ):
        forbid(upload, forbidden, "Marian Android runtime artifact upload section")

    if "quest3" in workflow.split("runs-on:", 1)[1].split("\n", 1)[0].casefold():
        raise GateError("Marian Android runtime workflow must not require a Quest runner label")

    return {
        "status": "pass",
        "arm64_android_device_required": True,
        "quest_device_required": False,
        "semantic_span_pipeline_runtime_required": True,
        "deterministic_fixture_configuration_required": True,
        "demo_defaults_can_change_without_redefining_smoke": True,
        "truthful_runtime_readiness_report_required": True,
        "exact_offline_reference_match_required": True,
        "device_resident_backend_required": True,
        "managed_tokenizer_runtime_required": True,
        "android_runtime_execution_required": True,
        "network_required": False,
        "raw_adb_serial_uploaded": False,
        "raw_process_logcat_uploaded": False,
        "apk_artifact_upload_allowed": False,
        "model_weight_artifact_upload_allowed": False,
        "redistribution_review_pending": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
