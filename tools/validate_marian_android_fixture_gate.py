#!/usr/bin/env python3
"""Static anti-false-positive contract for the pre-device Marian Android packaging gate."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / ".github/workflows/marian-unity-host-gate.yml"
SCENE_SETUP = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerMarianProductFixtureSetup.cs"
BUILD = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerMarianProductAndroidBuild.cs"
SHELL = ROOT / "tools/unity/build-marian-product-android-fixture.sh"
TOKENIZER_STAGER = ROOT / "tools/prepare_unity_tokenizer_runtime.py"
APK_INSPECTOR = ROOT / "tools/inspect_android_apk_structure.py"
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
    scene = SCENE_SETUP.read_text(encoding="utf-8")
    build = BUILD.read_text(encoding="utf-8")
    shell = SHELL.read_text(encoding="utf-8")
    tokenizer = TOKENIZER_STAGER.read_text(encoding="utf-8")
    apk_inspector = APK_INSPECTOR.read_text(encoding="utf-8")
    guarded_csproj = GUARDED_CSPROJ.read_text(encoding="utf-8")

    for fragment in (
        'PRESERVED_ASSEMBLIES = (',
        '"PhraseLayer.Tokenization.Microsoft"',
        '"Microsoft.ML.Tokenizers"',
        '"Google.Protobuf"',
        'LINK_XML_NAME = "link.xml"',
        'preserve="all"',
        '"il2cpp_reflection_preserve_required": True',
        '"reflection_entry_point": "PhraseLayer.Tokenization.Microsoft.MicrosoftMlMarianTokenizerFactory"',
    ):
        require(tokenizer, fragment, "managed tokenizer stager")

    for fragment in (
        'ScenePath = "Assets/Scenes/PhraseLayerMarianProductFixture.unity"',
        "root.AddComponent<PhraseLayerDemoBehaviour>()",
        "root.AddComponent<UnityMarianTranslationBootstrapBehaviour>()",
        "PhraseLayerLocalMarianAssets.EncoderPath",
        "PhraseLayerLocalMarianAssets.DecoderPath",
        "PhraseLayerLocalMarianAssets.DecoderWithPastPath",
        "bootstrap.SetSceneReferences(demo, encoder, decoder, decoderWithPast)",
        "bootstrap.SetTokenizerResourceRoot(PhraseLayerLocalMarianAssets.TokenizerResourceRoot)",
        "bootstrap.SetGenerationLimits(MaximumSourceTokens, MaximumTargetTokens)",
        "bootstrap.SetDeviceResidentCache(true)",
        "EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) }",
    ):
        require(scene, fragment, "Marian product fixture scene setup")

    for fragment in (
        'DefaultApplicationIdentifier = "com.unjuno.phraselayer.marianfixture"',
        'BuildPathEnvironment = "PHRASELAYER_MARIAN_PRODUCT_FIXTURE_APK_PATH"',
        "PhraseLayerLocalMarianAssets.VerifyLocalAssets()",
        "PhraseLayerMarianProductFixtureSetup.CreateScene()",
        "PlayerSettings.SetScriptingBackend(namedTarget, ScriptingImplementation.IL2CPP)",
        "PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64",
        "enabledScenes.Length != 1",
        "PhraseLayerMarianProductFixtureSetup.ScenePath",
        "phrase-layer-marian-product-android-fixture-build",
        "MarianOpusMtEnJa",
        "UnityMarianDeviceResidentGenerationBackend",
        "Microsoft.ML.Tokenizers",
        "product_translation_gate",
        "semantic_span_pipeline",
        "source_weight_copied_to_unity",
        "il2cpp_reflection_preserve_required",
        "redistribution_review",
        "apk_upload_allowed",
        "quest_device_execution_performed",
        "android_runtime_execution_performed",
        "deterministic_single_scene_build",
        "Application.dataPath",
    ):
        require(build, fragment, "Marian Android product builder")

    for fragment in (
        "UNITY_EDITOR must point to the Unity 6000.0.66f2 Editor executable.",
        "PHRASELAYER_MARIAN_PRODUCT_FIXTURE_APK_PATH",
        "Assets/LocalTokenizerRuntime/link.xml",
        "PhraseLayerMarianProductAndroidBuild.BuildBatch",
        "-nographics",
        "no model inference is executed during Android packaging",
        "APK must not be uploaded while redistribution review is pending",
    ):
        require(shell, fragment, "Marian Android build shell")

    for fragment in (
        "zipfile.is_zipfile",
        '"AndroidManifest.xml"',
        'abis != ["arm64-v8a"]',
        '"lib/arm64-v8a/libil2cpp.so"',
        '"lib/arm64-v8a/libunity.so"',
        'name.startswith("assets/bin/Data/")',
        '"model_asset_presence_proven_by_zip_structure": False',
        '"reflection_runtime_proven_by_zip_structure": False',
        '"runtime_execution_performed": False',
    ):
        require(apk_inspector, fragment, "Android APK structure inspector")

    for fragment in (
        "<DefineConstants>PHRASELAYER_UNITY_AI_INFERENCE_2_2</DefineConstants>",
        "PhraseLayerLocalMarianAssets.cs",
        "PhraseLayerMarianParityEvidence.cs",
        "PhraseLayerMarianProductFixtureSetup.cs",
        "PhraseLayerMarianProductAndroidBuild.cs",
    ):
        require(guarded_csproj, fragment, "guarded Marian compile project")

    for fragment in (
        "python tools/validate_marian_android_fixture_gate.py",
        "python tools/test_inspect_android_apk_structure.py",
        "Build local-only Marian Android ARM64 IL2CPP product fixture",
        "build-marian-product-android-fixture.sh",
        "PhraseLayerMarianProductFixture.apk",
        'assert data["architecture"] == "ARM64"',
        'assert data["scripting_backend"] == "IL2CPP"',
        'assert data["product_translation_gate"] is True',
        'assert data["apk_upload_allowed"] is False',
        'assert tokenizer["il2cpp_reflection_preserve_required"] is True',
        "inspect_android_apk_structure.py",
        "marian-product-apk-structure.json",
        'assert apk_struct["native_abis"] == ["arm64-v8a"]',
        'assert apk_struct["il2cpp_native_library_present"] is True',
        'assert apk_struct["unity_player_library_present"] is True',
        'assert apk_struct["model_asset_presence_proven_by_zip_structure"] is False',
        '"apk_structure_verified": True',
        '"android_arm64_il2cpp_fixture_build_passed": True',
        '"android_runtime_execution_performed": False',
        '"apk_uploaded": False',
        '"apk_removed_before_artifact_upload": True',
        'apk.unlink()',
        'Remove local Marian APK before artifact phase',
        'rm -f "$RUNNER_TEMP/PhraseLayerMarianProductFixture.apk"',
        "PhraseLayer.marian-product-fixture-build-evidence.json",
        "marian-product-apk-fingerprint.json",
    ):
        require(workflow, fragment, "Marian Unity host workflow")

    upload_section = workflow.split("- name: Upload Marian pre-device evidence only", 1)[1]
    for forbidden in (
        "PhraseLayerMarianProductFixture.apk",
        "encoder_model.onnx",
        "decoder_model.onnx",
        "decoder_with_past_model.onnx",
        "pytorch_model.bin",
    ):
        forbid(upload_section, forbidden, "Marian artifact upload section")

    return {
        "status": "pass",
        "android_arm64_il2cpp_build_required": True,
        "device_resident_backend_serialized": True,
        "managed_tokenizer_linker_preservation_required": True,
        "deterministic_translation_only_scene_required": True,
        "guarded_packaging_compile_required": True,
        "local_apk_fingerprint_required": True,
        "apk_zip_integrity_required": True,
        "apk_arm64_only_required": True,
        "apk_il2cpp_library_required": True,
        "apk_unity_data_required": True,
        "apk_structure_does_not_claim_model_runtime": True,
        "apk_artifact_upload_allowed": False,
        "model_weight_artifact_upload_allowed": False,
        "redistribution_review_pending": True,
        "android_runtime_execution_deferred": True,
        "quest_execution_deferred": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
