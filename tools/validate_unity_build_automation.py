#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path
import json
import sys

ROOT = Path(__file__).resolve().parents[1]
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
HOOK = UNITY / "Assets" / "Editor" / "PhraseLayerCloudBuildVerification.cs"
QUEST_GUARD = UNITY / "Assets" / "Editor" / "PhraseLayerQuestMrBuildGuard.cs"
EDITOR_VERIFY = UNITY / "Assets" / "Editor" / "PhraseLayerEditorVerification.cs"
EDITOR_SETUP = UNITY / "Assets" / "Editor" / "PhraseLayerEditorSetup.cs"
EDITOR_ASMDEF = UNITY / "Assets" / "Editor" / "PhraseLayer.Unity.Editor.asmdef"
PROJECT_VERSION = UNITY / "ProjectSettings" / "ProjectVersion.txt"
MANIFEST = UNITY / "Packages" / "manifest.json"
DOC = ROOT / "docs" / "UNITY_BUILD_AUTOMATION.md"

EXPECTED_METHOD = "PhraseLayer.Unity.Editor.PhraseLayerCloudBuildVerification.PreExport"
EXPECTED_UNITY = "6000.0.66f2"
EXPECTED_SUBFOLDER = "unity/PhraseLayer.Unity"
EXPECTED_BRANCH = "agent/multi-sentence-segmentation"
EXPECTED_META_PACKAGE = "com.meta.xr.mrutilitykit"
EXPECTED_META_VERSION = "85.0.0"
EXPECTED_CORE_PACKAGE_PATH = "file:../../../src/PhraseLayer.Core"
EXPECTED_INFERENCE_DEFINE = {
    "name": "com.unity.ai.inference",
    "expression": "[2.2.1,2.3.0)",
    "define": "PHRASELAYER_UNITY_AI_INFERENCE_2_2",
}


def require(condition: bool, message: str, errors: list[str]) -> None:
    if not condition:
        errors.append(message)


def main() -> int:
    errors: list[str] = []
    for path in (HOOK, QUEST_GUARD, EDITOR_VERIFY, EDITOR_SETUP, EDITOR_ASMDEF, PROJECT_VERSION, MANIFEST, DOC):
        require(path.exists(), f"missing required UBA file: {path.relative_to(ROOT)}", errors)
    if errors:
        for error in errors:
            print(f"ERROR: {error}")
        return 1

    hook = HOOK.read_text(encoding="utf-8")
    quest_guard = QUEST_GUARD.read_text(encoding="utf-8")
    verify = EDITOR_VERIFY.read_text(encoding="utf-8")
    setup = EDITOR_SETUP.read_text(encoding="utf-8")
    editor_asmdef = json.loads(EDITOR_ASMDEF.read_text(encoding="utf-8"))
    version = PROJECT_VERSION.read_text(encoding="utf-8")
    doc = DOC.read_text(encoding="utf-8")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))

    for marker, message in (
        ("IPreprocessBuildWithReport", "UBA gate must retain automatic player-build verification"),
        ("OnPreprocessBuild(BuildReport report)", "UBA gate must implement preprocess callback"),
        ("public static void PreExport()", "UBA gate must expose parameterless PreExport entry point"),
        ("PhraseLayerEditorVerification.VerifyCorePipeline();", "UBA gate must execute Unity verification"),
        ("PhraseLayerEditorSetup.CreateDemoScene();", "UBA PreExport must recover from no enabled build scene"),
        ("EnsureEnabledBuildSceneOrFail();", "player-build gate must fail closed when scene preparation did not occur"),
        ("callbackOrder => -9000", "wider UBA gate ordering must remain after local-only and Quest/MR guards"),
    ):
        require(marker in hook, message, errors)

    require(
        hook.count("PhraseLayerQuestMrBuildGuard.VerifyQuestMrContract();") >= 2,
        "Quest/MR contract must run both from UBA PreExport and immediately before Android player build",
        errors,
    )
    require(
        "callbackOrder => -9500" in quest_guard,
        "Quest/MR build guard must run after local-only (-10000) and before wider UBA gate (-9000)",
        errors,
    )
    for marker, message in (
        ("MetaXRFeature Android", "Quest/MR guard must verify the Android Meta XR Feature"),
        ("XR_META_environment_raycast", "Quest/MR guard must verify the environment-raycast OpenXR extension"),
        ("com.oculus.permission.USE_SCENE", "Quest/MR guard must verify local Spatial Data permission"),
        ("MRUKNativeFuncs", "Quest/MR guard must verify IL2CPP preservation for the native MRUK boundary"),
    ):
        require(marker in quest_guard, message, errors)

    require("VerifyCorePipeline" in verify, "editor verification entry point missing", errors)
    require("EditorBuildSettings.scenes" in setup, "editor setup must configure build scenes", errors)
    require(EXPECTED_UNITY in version, f"Unity project must remain pinned to {EXPECTED_UNITY}", errors)

    # The pre-export verification itself lives in the Editor assembly and uses the same conditional
    # Unity Inference code path as the runtime assembly. versionDefines are assembly-local in Unity, so
    # the Editor asmdef must declare the define explicitly or the gate always executes its unsupported path.
    require(
        EXPECTED_INFERENCE_DEFINE in editor_asmdef.get("versionDefines", []),
        "PhraseLayer.Unity.Editor.asmdef must define PHRASELAYER_UNITY_AI_INFERENCE_2_2 for com.unity.ai.inference 2.2.x",
        errors,
    )
    require(
        "Unity.InferenceEngine" in editor_asmdef.get("references", []),
        "PhraseLayer.Unity.Editor.asmdef must directly reference Unity.InferenceEngine when its guarded editor code uses ModelAsset/BackendType",
        errors,
    )

    # Meta's current Passthrough Camera sample resolves MRUK directly from Unity Package Manager and does
    # not install a custom scoped registry. Keep the manifest equally simple so unattended UBA import does
    # not depend on an unnecessary alternate registry/authentication path.
    require(
        not manifest.get("scopedRegistries"),
        "Unity manifest must not add a custom Meta scoped registry for MRUK; use the standard package resolution path",
        errors,
    )

    dependencies = manifest.get("dependencies", {})
    require(
        dependencies.get(EXPECTED_META_PACKAGE) == EXPECTED_META_VERSION,
        f"{EXPECTED_META_PACKAGE} must remain pinned to {EXPECTED_META_VERSION} for this gate",
        errors,
    )
    core_package_ref = dependencies.get("com.unjuno.phraselayer.core")
    require(
        core_package_ref == EXPECTED_CORE_PACKAGE_PATH,
        "local PhraseLayer.Core package path must be relative to the Packages directory and resolve to repository src/PhraseLayer.Core",
        errors,
    )
    if isinstance(core_package_ref, str) and core_package_ref.startswith("file:"):
        resolved_core = (MANIFEST.parent / core_package_ref[len("file:"):]).resolve()
        expected_core = (ROOT / "src" / "PhraseLayer.Core").resolve()
        require(
            resolved_core == expected_core,
            f"local PhraseLayer.Core package resolves to {resolved_core}, expected {expected_core}",
            errors,
        )

    for value, label in (
        (EXPECTED_METHOD, "required Pre-Export method"),
        (EXPECTED_SUBFOLDER, "project subfolder"),
        (EXPECTED_BRANCH, "branch"),
        (EXPECTED_UNITY, "Unity version"),
        ("Android", "platform"),
        ("SDK 36", "Android SDK"),
        ("standard Unity Package Manager resolution", "Meta package resolution"),
        ("Required Pre-Export hook", "required hook behavior"),
    ):
        require(value in doc, f"UBA documentation must state {label}: {value}", errors)

    for forbidden in ("UNITY_LICENSE", "UNITY_EMAIL", "UNITY_PASSWORD"):
        require(forbidden not in doc, f"UBA documentation must not require legacy secret {forbidden}", errors)

    if errors:
        for error in errors:
            print(f"ERROR: {error}")
        return 1

    print("Unity Build Automation contract PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
