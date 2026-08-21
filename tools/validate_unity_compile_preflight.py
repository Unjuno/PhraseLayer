#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path
import json
import sys

ROOT = Path(__file__).resolve().parents[1]
SHELL = ROOT / "tests" / "PhraseLayer.UnityShell.Compile"
EDITOR_CSPROJ = SHELL / "PhraseLayer.UnityShell.Compile.csproj"
ANDROID_CSPROJ = SHELL / "PhraseLayer.UnityAndroid.Compile.csproj"
UNITY_STUBS = SHELL / "UnityStubs.cs"
ANDROID_STUBS = SHELL / "UnityAndroidStubs.cs"
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
    editor_csproj = read(EDITOR_CSPROJ)
    android_csproj = read(ANDROID_CSPROJ)
    unity_stubs = read(UNITY_STUBS)
    android_stubs = read(ANDROID_STUBS)
    inference_stubs = read(INFERENCE_STUBS)

    for marker in (
        "UNITY_5_3_OR_NEWER",
        "UNITY_EDITOR",
        "PHRASELAYER_UNITY_AI_INFERENCE_2_2",
        "../../unity/PhraseLayer.Unity/Assets/Scripts/**/*.cs",
        "../../unity/PhraseLayer.Unity/Assets/Editor/**/*.cs",
        "TreatWarningsAsErrors>true",
        "<BaseIntermediateOutputPath>obj/Editor/</BaseIntermediateOutputPath>",
        "<OutputPath>bin/Editor/</OutputPath>",
    ):
        require(marker in editor_csproj, f"Unity Editor shell compile project missing required marker: {marker}")

    for marker in (
        "UNITY_5_3_OR_NEWER",
        "UNITY_ANDROID",
        "PHRASELAYER_UNITY_AI_INFERENCE_2_2",
        "../../unity/PhraseLayer.Unity/Assets/Scripts/**/*.cs",
        "TreatWarningsAsErrors>true",
        "<BaseIntermediateOutputPath>obj/Android/</BaseIntermediateOutputPath>",
        "<OutputPath>bin/Android/</OutputPath>",
    ):
        require(marker in android_csproj, f"Unity Android compile project missing required marker: {marker}")

    require(
        "UNITY_EDITOR" not in android_csproj,
        "Android Player compile preflight must not define UNITY_EDITOR; otherwise Quest-only branches remain hidden",
    )
    require(
        "Assets/Editor/**/*.cs" not in android_csproj,
        "Android Player compile preflight must compile runtime scripts only",
    )

    for marker in (
        "public sealed class RenderTexture : Texture",
        "public sealed class Texture2D : Texture",
        "public static class Graphics",
        "public static class JsonUtility",
        "public sealed class SerializedObject",
        "public static class EditorUtility",
        "FindObjectsOfTypeAll<T>",
        "public byte[] bytes",
    ):
        require(marker in unity_stubs, f"Unity stubs missing real-branch compile surface: {marker}")

    for marker in (
        "namespace UnityEngine.Android",
        "PermissionCallbacks",
        "HasUserAuthorizedPermission",
        "RequestUserPermissions",
    ):
        require(marker in android_stubs, f"Android compile stubs missing permission surface: {marker}")

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
        "PASS: Unity compile preflight covers isolated Editor and Android Player guarded branches before UBA"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
