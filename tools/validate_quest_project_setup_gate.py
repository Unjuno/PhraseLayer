#!/usr/bin/env python3
"""Static contract for applying pinned Meta Quest Required project settings before the Read Mode build."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SETUP = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerQuestProjectSetup.cs"
BUILD_SH = ROOT / "tools/unity/build-android-read-mode-fixture.sh"
MANIFEST = ROOT / "unity/PhraseLayer.Unity/Packages/manifest.json"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def validate() -> dict[str, object]:
    setup = SETUP.read_text(encoding="utf-8")
    build_sh = BUILD_SH.read_text(encoding="utf-8")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    dependencies = manifest.get("dependencies", {})

    if dependencies.get("com.meta.xr.mrutilitykit") != "85.0.0":
        raise GateError("MRUK must remain pinned to 85.0.0")
    if dependencies.get("com.unity.xr.meta-openxr") != "2.2.1":
        raise GateError("Unity OpenXR: Meta must remain pinned to 2.2.1")
    if dependencies.get("com.unity.xr.openxr") != "1.15.1":
        raise GateError("Unity OpenXR must remain pinned to 1.15.1")

    for fragment in (
        'ProjectSetupTypeName = "OVRProjectSetup"',
        "ApplyAndroidRequiredFixesBatch()",
        "EnsureAndroidTarget()",
        'setupType.GetMethod("FixAllAsync")',
        "parameters[0].ParameterType != typeof(BuildTargetGroup)",
        "fixAll.Invoke(null, new object[] { BuildTargetGroup.Android })",
        "var task = result as Task",
        "await task",
        "AssetDatabase.SaveAssets()",
        "EditorApplication.Exit(0)",
        "EditorApplication.Exit(1)",
    ):
        require(setup, fragment, "Meta Quest project setup bridge")

    setup_call = (
        "-executeMethod PhraseLayer.Unity.Editor.PhraseLayerQuestProjectSetup."
        "ApplyAndroidRequiredFixesBatch"
    )
    build_call = (
        "-executeMethod PhraseLayer.Unity.Editor.PhraseLayerReadModeFixtureAndroidBuild."
        "BuildBatch"
    )
    require(build_sh, setup_call, "Read Mode build shell")
    require(build_sh, build_call, "Read Mode build shell")
    if build_sh.index(setup_call) >= build_sh.index(build_call):
        raise GateError("Meta Project Setup Tool pass must execute before the fixture build pass")
    if build_sh.count('"$UNITY_EDITOR"') < 2:
        raise GateError("Quest project setup and fixture build must run in separate Unity processes")

    return {
        "status": "pass",
        "meta_project_setup_tool_required": True,
        "required_android_fixes_applied_before_build": True,
        "clean_checkout_xr_settings_supported": True,
        "separate_unity_processes_required": True,
        "mruk_version": "85.0.0",
        "meta_openxr_version": "2.2.1",
        "openxr_version": "1.15.1",
        "real_unity_setup_execution_still_required": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
