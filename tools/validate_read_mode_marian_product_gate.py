#!/usr/bin/env python3
"""Static contract for the pre-device combined Read Mode + Marian product packaging gate."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SETUP = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerReadModeMarianProductFixtureSetup.cs"
EDITOR_SETUP = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerEditorSetup.cs"
VISUALS = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerLocalReadModeVisualAssets.cs"
BUILD = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerReadModeMarianProductAndroidBuild.cs"
SCRIPT = ROOT / "tools/unity/build-read-mode-marian-product-fixture.sh"
WORKFLOW = ROOT / ".github/workflows/read-mode-marian-unity-host-gate.yml"
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
    setup = SETUP.read_text(encoding="utf-8")
    editor_setup = EDITOR_SETUP.read_text(encoding="utf-8")
    visuals = VISUALS.read_text(encoding="utf-8")
    build = BUILD.read_text(encoding="utf-8")
    script = SCRIPT.read_text(encoding="utf-8")
    workflow = WORKFLOW.read_text(encoding="utf-8")
    guarded = GUARDED_CSPROJ.read_text(encoding="utf-8")

    for fragment in (
        "Action<GameObject, PhraseLayerDemoBehaviour> configureRoot",
        "configureRoot?.Invoke(root, demo)",
        "The optional root configurator is a narrow extension point",
    ):
        require(editor_setup, fragment, "Read Mode editor setup translation extension point")

    for fragment in (
        "Action<GameObject, PhraseLayerDemoBehaviour> configureRoot",
        "PhraseLayerEditorSetup.CreateDemoScene(",
        "configureRoot)",
        "product_root_configurator=",
    ):
        require(visuals, fragment, "Read Mode visual asset staging extension point")

    for fragment in (
        "PhraseLayerLocalMarianAssets.VerifyLocalAssets()",
        "autoRunQuestReadModeSmoke: false",
        "configureRoot: ConfigureMarianProductTranslation",
        "PhraseLayerLocalOcrAssets.AssignLocalAssetsToSceneBootstrap()",
        "root.AddComponent<UnityMarianTranslationBootstrapBehaviour>()",
        "bootstrap.SetSceneReferences(demo, encoder, decoder, decoderWithPast)",
        "bootstrap.SetTokenizerResourceRoot(PhraseLayerLocalMarianAssets.TokenizerResourceRoot)",
        "bootstrap.SetDeviceResidentCache(true)",
        "demo.SetAutoRunOnStart(true)",
        "Quest smoke autorun=false",
        "runtime execution not performed by scene setup",
    ):
        require(setup, fragment, "combined Read Mode + Marian scene setup")

    for fragment in (
        'DefaultApplicationIdentifier = "com.unjuno.phraselayer.readmodemarianfixture"',
        'BuildPathEnvironment = "PHRASELAYER_READ_MODE_MARIAN_PRODUCT_APK_PATH"',
        'MetaProjectSetupEnvironment = "PHRASELAYER_META_PROJECT_SETUP_APPLIED"',
        "RequireMetaProjectSetupHandshake()",
        'Environment.GetEnvironmentVariable(MetaProjectSetupEnvironment)',
        '"1"',
        "PhraseLayerReadModeMarianProductFixtureSetup.CreateScene()",
        "PlayerSettings.SetScriptingBackend(namedTarget, ScriptingImplementation.IL2CPP)",
        "PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64",
        '\\"schema_version\\": 2',
        '\\"purpose\\": \\"phrase-layer-read-mode-marian-product-android-fixture-build',
        '\\"meta_project_setup_applied_before_build\\": true',
        '\\"meta_project_setup_separate_unity_process_required\\": true',
        '\\"ocr_runtime\\": \\"PaddleOCR\\"',
        '\\"surface_runtime\\": \\"MRUKEnvironmentRaycast\\"',
        '\\"translation_runtime\\": \\"MarianOpusMtEnJa\\"',
        '\\"generation_backend\\": \\"UnityMarianDeviceResidentGenerationBackend\\"',
        '\\"tokenizer_runtime\\": \\"Microsoft.ML.Tokenizers\\"',
        '\\"product_translation_gate\\": true',
        '\\"combined_single_scene_packaging\\": true',
        '\\"source_weight_copied_to_unity\\": false',
        '\\"camera_pixel_pose_sync_verified\\": false',
        '\\"quest_read_mode_smoke_autorun\\": false',
        '\\"quest_device_execution_performed\\": false',
        '\\"android_runtime_execution_performed\\": false',
        '\\"ocr_redistribution_review\\": \\"pending\\"',
        '\\"translation_redistribution_review\\": \\"pending\\"',
        '\\"apk_upload_allowed\\": false',
        "VisualEvidenceRelativePath",
        "OcrManifestRelativePath",
        "MarianManifestRelativePath",
        "LinkerDescriptorRelativePath",
    ):
        require(build, fragment, "combined Android packaging build")

    for fragment in (
        "PHRASELAYER_JAPANESE_FONT_SOURCE",
        "PHRASELAYER_READ_MODE_MARIAN_PRODUCT_APK_PATH",
        "Assets/LocalOcrAssets/PaddleOCR/detection.onnx",
        "Assets/LocalTranslationAssets/Marian/encoder_model.onnx",
        "Assets/LocalTokenizerRuntime/link.xml",
        "unset PHRASELAYER_META_PROJECT_SETUP_APPLIED",
        "PhraseLayer.Unity.Editor.PhraseLayerQuestProjectSetup.ApplyAndroidRequiredFixesBatch",
        "export PHRASELAYER_META_PROJECT_SETUP_APPLIED=1",
        "dedicated Unity process",
        "-nographics",
        "PhraseLayer.Unity.Editor.PhraseLayerReadModeMarianProductAndroidBuild.BuildBatch",
        "no Quest/runtime PASS is implied",
    ):
        require(script, fragment, "combined Unity build runner")

    setup_index = script.index("PhraseLayer.Unity.Editor.PhraseLayerQuestProjectSetup.ApplyAndroidRequiredFixesBatch")
    handshake_index = script.index("export PHRASELAYER_META_PROJECT_SETUP_APPLIED=1")
    build_index = script.index("PhraseLayer.Unity.Editor.PhraseLayerReadModeMarianProductAndroidBuild.BuildBatch")
    if not setup_index < handshake_index < build_index:
        raise GateError("combined Unity build runner must apply Meta setup, set the success handshake, then start a fresh build process")

    for fragment in (
        "workflow_dispatch:",
        "japanese_font_source:",
        "marian_source_snapshot:",
        "marian_onnx_dir:",
        "runs-on: [self-hosted, unity, unity-6000-0-66f2]",
        "Run real Unity host capability preflight",
        "Download and stage exact pinned PP-OCR assets",
        "Require real Unity pinned PP-OCR inference and GPU preprocess parity",
        "Validate local Marian source snapshot and ONNX bundle identity",
        "Require real Unity exact-token Marian translation parity",
        "Build one local-only Read Mode plus Marian Android ARM64 IL2CPP APK",
        "build-read-mode-marian-product-fixture.sh",
        'data["combined_single_scene_packaging"] is True',
        'data["quest_read_mode_smoke_autorun"] is False',
        'data["quest_device_execution_performed"] is False',
        'data["android_runtime_execution_performed"] is False',
        'data["camera_pixel_pose_sync_verified"] is False',
        'structure["native_abis"] == ["arm64-v8a"]',
        "apk.unlink()",
        "Remove local combined APK before artifact phase",
        "Upload safe combined host evidence only",
        "phraselayer-read-mode-marian-unity-host-evidence",
    ):
        require(workflow, fragment, "combined self-hosted Unity workflow")

    upload = workflow.split("- name: Upload safe combined host evidence only", 1)[1]
    for forbidden in (
        "PhraseLayerReadModeMarianProductFixture.apk",
        "encoder_model.onnx",
        "decoder_model.onnx",
        "decoder_with_past_model.onnx",
        "detection.onnx",
        "recognition.onnx",
        "pytorch_model.bin",
        "marian-reference.json",
        "**",
    ):
        forbid(upload, forbidden, "combined host artifact upload section")

    for fragment in (
        "PhraseLayerReadModeMarianProductFixtureSetup.cs",
        "PhraseLayerReadModeMarianProductAndroidBuild.cs",
    ):
        require(guarded, fragment, "guarded Marian compile project")

    return {
        "status": "pass",
        "combined_single_scene_packaging_required": True,
        "pinned_ppocr_real_unity_parity_required": True,
        "pinned_marian_real_unity_parity_required": True,
        "meta_project_setup_separate_process_required": True,
        "meta_project_setup_success_handshake_required": True,
        "android_arm64_il2cpp_required": True,
        "product_translation_gate": True,
        "quest_read_mode_smoke_autorun": False,
        "quest_device_execution_required": False,
        "android_runtime_execution_required": False,
        "pixel_pose_sync_claim_allowed": False,
        "apk_artifact_upload_allowed": False,
        "model_weight_artifact_upload_allowed": False,
        "redistribution_review_pending": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
