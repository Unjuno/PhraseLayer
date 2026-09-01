#!/usr/bin/env python3
"""Static contract for the real-Unity host capability preflight used before model-heavy gates."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EDITOR = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerUnityHostPreflight.cs"
SHELL = ROOT / "tools/unity/run-host-preflight.sh"
READ_WORKFLOW = ROOT / ".github/workflows/read-mode-unity-host-gate.yml"
MARIAN_WORKFLOW = ROOT / ".github/workflows/marian-unity-host-gate.yml"
MARIAN_ANDROID_WORKFLOW = ROOT / ".github/workflows/marian-android-runtime-smoke.yml"
QUEST_WORKFLOW = ROOT / ".github/workflows/quest3-read-mode-smoke.yml"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def compact(text: str) -> str:
    return "".join(text.split())


def forbid(text: str, fragment: str, label: str) -> None:
    if fragment in text:
        raise GateError(f"{label} contains forbidden marker: {fragment}")


def validate() -> dict[str, object]:
    editor = EDITOR.read_text(encoding="utf-8")
    shell = SHELL.read_text(encoding="utf-8")
    read_workflow = READ_WORKFLOW.read_text(encoding="utf-8")
    marian_workflow = MARIAN_WORKFLOW.read_text(encoding="utf-8")
    marian_android_workflow = MARIAN_ANDROID_WORKFLOW.read_text(encoding="utf-8")
    quest_workflow = QUEST_WORKFLOW.read_text(encoding="utf-8")

    for fragment in (
        'ExpectedUnityVersion = "6000.0.66f2"',
        "Application.unityVersion",
        "BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android)",
        "IsInferenceCompileGateActive()",
        "#if PHRASELAYER_UNITY_AI_INFERENCE_2_2",
        "return true;",
        "return false;",
        "Packages\", \"manifest.json",
        "ProjectSettings\", \"ProjectVersion.txt",
        "Application.dataPath",
        "phrase-layer-real-unity-host-preflight",
        "real_unity_execution",
        "exact_unity_version_match",
        "android_build_support_available",
        "inference_engine_compile_gate_active",
        "local_asset_paths_serialized",
        "adb_required",
        "quest_device_execution_performed",
        "File.WriteAllText(evidencePath, json)",
        "RunBatch()",
    ):
        require(editor, fragment, "Unity host preflight Editor gate")

    for fragment in (
        "UNITY_EDITOR must point to the Unity 6000.0.66f2 Editor executable.",
        "PHRASELAYER_UNITY_HOST_PREFLIGHT_EVIDENCE_PATH",
        "-nographics",
        "PhraseLayerUnityHostPreflight.RunBatch",
        "Unity host preflight evidence was not produced",
        "real Unity host capability preflight",
    ):
        require(shell, fragment, "Unity host preflight shell")
    for forbidden in ("adb ", "run_quest", "PHRASELAYER_DEVICE_SERIAL"):
        forbid(shell, forbidden, "Unity host preflight shell")

    workflow_markers = (
        "Run real Unity host capability preflight",
        "run-host-preflight.sh",
        "PHRASELAYER_UNITY_HOST_PREFLIGHT_EVIDENCE_PATH:",
        "Require Unity host preflight evidence",
        'data["purpose"] == "phrase-layer-real-unity-host-preflight"',
        'data["real_unity_execution"] is True',
        'data["exact_unity_version_match"] is True',
        'data["android_build_support_available"] is True',
        'data["inference_engine_compile_gate_active"] is True',
        'data["local_asset_paths_serialized"] is False',
        'data["adb_required"] is False',
        'data["quest_device_execution_performed"] is False',
    )
    for workflow, label in (
        (read_workflow, "Read Mode host workflow"),
        (marian_workflow, "Marian host workflow"),
        (marian_android_workflow, "Marian Android runtime workflow"),
        (quest_workflow, "Quest Read Mode workflow"),
    ):
        compact_workflow = compact(workflow)
        for fragment in workflow_markers:
            require(compact_workflow, compact(fragment), label)

    return {
        "status": "pass",
        "expected_unity_version": "6000.0.66f2",
        "runner_label_is_not_sufficient_evidence": True,
        "real_unity_execution_required": True,
        "android_build_support_required": True,
        "inference_compile_gate_required": True,
        "local_paths_redacted": True,
        "host_workflows_covered": 4,
        "adb_dependency": False,
        "quest_execution": False,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
