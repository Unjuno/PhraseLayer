#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path
import json
import sys

ROOT = Path(__file__).resolve().parents[1]
SHELL = ROOT / "tests" / "PhraseLayer.UnityShell.Compile"
CSPROJ = SHELL / "PhraseLayer.UnityShell.Compile.csproj"
UNITY_STUBS = SHELL / "UnityStubs.cs"
INFERENCE_STUBS = SHELL / "UnityInferenceStubs.cs"
RUNTIME_ASMDEF = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "PhraseLayer.Unity.asmdef"
EDITOR_ASMDEF = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Editor" / "PhraseLayer.Unity.Editor.asmdef"

errors: list[str] = []


def require(condition: bool, message: str) -> None:
    if not condition:
        errors.append(message)


def read(path: Path) -> str:
    if not path.is_file():
        errors.append(f"missing compile-preflight file: {path.relative_to(ROOT)}")
        return ""
    return path.read_text(encoding="utf-8")


def validate_asmdef(path: Path) -> None:
    if not path.is_file():
        errors.append(f"missing asmdef: {path.relative_to(ROOT)}")
        return
    data = json.loads(path.read_text(encoding="utf-8"))
    defines = data.get("versionDefines", [])
    require(
        any(
            item.get("name") == "com.unity.ai.inference"
            and item.get("expression") == "[2.2.1,2.3.0)"
            and item.get("define") == "PHRASELAYER_UNITY_AI_INFERENCE_2_2"
            for item in defines
        ),
        f"{path.name} must mirror the reviewed Unity Inference 2.2.x version define",
    )
    require(
        "Unity.InferenceEngine" in data.get("references", []),
        f"{path.name} must directly reference Unity.InferenceEngine",
    )


def main() -> int:
    csproj = read(CSPROJ)
    unity_stubs = read(UNITY_STUBS)
    inference_stubs = read(INFERENCE_STUBS)

    for marker in (
        "UNITY_5_3_OR_NEWER",
        "UNITY_EDITOR",
        "PHRASELAYER_UNITY_AI_INFERENCE_2_2",
        "../../unity/PhraseLayer.Unity/Assets/Scripts/**/*.cs",
        "../../unity/PhraseLayer.Unity/Assets/Editor/**/*.cs",
        "TreatWarningsAsErrors>true",
    ):
        require(marker in csproj, f"Unity shell compile project missing required preflight marker: {marker}")

    for marker in (
        "public sealed class RenderTexture : Texture",
        "public sealed class Texture2D : Texture",
        "public static class Graphics",
        "public static class JsonUtility",
        "public sealed class SerializedObject",
        "public static class EditorUtility",
        "FindObjectsOfTypeAll<T>",
    ):
        require(marker in unity_stubs, f"Unity stubs missing real-branch compile surface: {marker}")

    for marker in (
        "namespace Unity.InferenceEngine",
        "public sealed class ModelAsset",
        "public static class ModelLoader",
        "public sealed class Worker",
        "public sealed class Tensor<T>",
        "ReadbackAndClone()",
        "DownloadToArray()",
        "GPUCompute",
    ):
        require(marker in inference_stubs, f"Unity Inference 2.2 compile stub missing marker: {marker}")

    validate_asmdef(RUNTIME_ASMDEF)
    validate_asmdef(EDITOR_ASMDEF)

    if errors:
        for error in errors:
            print(f"ERROR: {error}")
        return 1

    print(
        "PASS: Unity compile preflight covers runtime + Editor sources with real Unity/Inference guarded branches enabled"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
