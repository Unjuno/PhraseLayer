#!/usr/bin/env python3
"""Static wiring gate for the real-Unity Android build path.

This does not pretend to compile or build Unity. It ensures the repository keeps the manual/local
real-Unity gate pinned to the reviewed Editor version, Android ARM64, IL2CPP, and both required offline
model stacks. The actual build result must still come from Unity 6000.0.66f2 with Android modules.
"""

from __future__ import annotations

import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
PROJECT_VERSION = ROOT / "unity/PhraseLayer.Unity/ProjectSettings/ProjectVersion.txt"
BUILD_SOURCE = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerAndroidBuild.cs"
LOCAL_SCRIPT = ROOT / "tools/unity/build-android-listen-mode.sh"
WORKFLOW = ROOT / ".github/workflows/android-il2cpp-listen-mode.yml"
EXPECTED_UNITY = "6000.0.66f2"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def validate() -> dict[str, object]:
    version = PROJECT_VERSION.read_text(encoding="utf-8")
    source = BUILD_SOURCE.read_text(encoding="utf-8")
    script = LOCAL_SCRIPT.read_text(encoding="utf-8")
    workflow = WORKFLOW.read_text(encoding="utf-8")

    require(version, f"m_EditorVersion: {EXPECTED_UNITY}", "Unity project version")

    for fragment in (
        "BuildTarget.Android",
        "NamedBuildTarget.Android",
        "ScriptingImplementation.IL2CPP",
        "AndroidArchitecture.ARM64",
        "PhraseLayerEditorSetup.CreateDemoScene()",
        "PhraseLayerLocalTranslationAssets.AssignLocalAssetsToDemo()",
        "PhraseLayerLocalAsrAssets.AssignLocalAssetsToSceneBootstrap()",
        '"translation_runtime": \\"Marian\\"',
        '"asr_runtime": \\"MoonshineV1\\"',
        '"dictionary_fallback_allowed": false',
        "BuildPipeline.BuildPlayer",
        "PhraseLayer.android-build-evidence.json",
    ):
        require(source, fragment, "Android build source")

    for fragment in (
        "UNITY_EDITOR",
        "PhraseLayer.Unity.Editor.PhraseLayerAndroidBuild.BuildBatch",
        "PHRASELAYER_ANDROID_BUILD_PATH",
        "PhraseLayer.android-build-evidence.json",
    ):
        require(script, fragment, "local Android build script")

    for fragment in (
        "workflow_dispatch:",
        "marian_source_dir:",
        "runs-on: [self-hosted, unity, unity-6000-0-66f2]",
        "tools/requirements-marian-export.txt",
        "stage-marian-runtime.sh",
        "moonshine-ai/moonshine",
        "390624ed33d594443aa4aa221f5b9f283b545b5a",
        "build-android-listen-mode.sh",
        'assert data["architecture"] == "ARM64"',
        'assert data["scripting_backend"] == "IL2CPP"',
        'assert data["translation_runtime"] == "Marian"',
        'assert data["asr_runtime"] == "MoonshineV1"',
        'assert data["dictionary_fallback_allowed"] is False',
    ):
        require(workflow, fragment, "Android build workflow")

    return {
        "status": "pass",
        "unity_version": EXPECTED_UNITY,
        "target": "Android",
        "architecture": "ARM64",
        "scripting_backend": "IL2CPP",
        "translation_runtime": "Marian",
        "asr_runtime": "MoonshineV1",
        "dictionary_fallback_allowed": False,
        "execution_scope": "real-unity-self-hosted-or-local",
    }


def main() -> None:
    import json
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
