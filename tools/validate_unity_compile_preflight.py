#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path
import json
import sys

ROOT = Path(__file__).resolve().parents[1]
SHELL = ROOT / "tests" / "PhraseLayer.UnityShell.Compile"
DIRECTORY_PROPS = SHELL / "Directory.Build.props"
EDITOR_CSPROJ = SHELL / "PhraseLayer.UnityShell.Compile.csproj"
ANDROID_CSPROJ = SHELL / "PhraseLayer.UnityAndroid.Compile.csproj"
UNITY_STUBS = SHELL / "UnityStubs.cs"
ANDROID_STUBS = SHELL / "UnityAndroidStubs.cs"
INFERENCE_STUBS = SHELL / "UnityInferenceStubs.cs"
UNITY_SCRIPTS = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts"
DETECTOR_RUNTIME = UNITY_SCRIPTS / "UnityPaddleOcrDetectorRuntime.cs"
RECOGNIZER_RUNTIME = UNITY_SCRIPTS / "UnityPaddleOcrRecognizerRuntime.cs"
RUNTIME_ASMDEF = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "PhraseLayer.Unity.asmdef"
EDITOR_ASMDEF = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Editor" / "PhraseLayer.Unity.Editor.asmdef"
WORKFLOW = ROOT / ".github" / "workflows" / "core-ci.yml"

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
    constraints = data.get("defineConstraints", [])
    require(
        "!UNITY_6000_0_OR_NEWER" not in constraints,
        f"{path.name} must not exclude the pinned Unity 6000 editor",
    )
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
    directory_props = read(DIRECTORY_PROPS)
    editor_csproj = read(EDITOR_CSPROJ)
    android_csproj = read(ANDROID_CSPROJ)
    unity_stubs = read(UNITY_STUBS)
    android_stubs = read(ANDROID_STUBS)
    inference_stubs = read(INFERENCE_STUBS)
    detector_runtime = read(DETECTOR_RUNTIME)
    recognizer_runtime = read(RECOGNIZER_RUNTIME)
    workflow = read(WORKFLOW)

    for marker in (
        "PhraseLayer.UnityShell.Compile",
        "obj/Editor/",
        "MSBuildProjectExtensionsPath",
        "PhraseLayer.UnityAndroid.Compile",
        "obj/Android/",
    ):
        require(marker in directory_props, f"compile preflight Directory.Build.props missing isolation marker: {marker}")

    for marker in (
        "<LangVersion>9.0</LangVersion>",
        "UNITY_5_3_OR_NEWER",
        "UNITY_EDITOR",
        "PHRASELAYER_UNITY_AI_INFERENCE_2_2",
        "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>",
        '<Compile Include="UnityStubs.cs" />',
        '<Compile Include="UnityInferenceStubs.cs" />',
        "../../unity/PhraseLayer.Unity/Assets/Scripts/**/*.cs",
        "../../unity/PhraseLayer.Unity/Assets/Editor/**/*.cs",
        "TreatWarningsAsErrors>true",
        "<OutputPath>bin/Editor/</OutputPath>",
    ):
        require(marker in editor_csproj, f"Unity Editor shell compile project missing required marker: {marker}")

    for marker in (
        "<LangVersion>9.0</LangVersion>",
        "UNITY_5_3_OR_NEWER",
        "UNITY_ANDROID",
        "PHRASELAYER_UNITY_AI_INFERENCE_2_2",
        "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>",
        '<Compile Include="UnityStubs.cs" />',
        '<Compile Include="UnityInferenceStubs.cs" />',
        '<Compile Include="UnityAndroidStubs.cs" />',
        "../../unity/PhraseLayer.Unity/Assets/Scripts/**/*.cs",
        "TreatWarningsAsErrors>true",
        "<OutputPath>bin/Android/</OutputPath>",
    ):
        require(marker in android_csproj, f"Unity Android compile project missing required marker: {marker}")

    require(
        "UnityAndroidStubs.cs" not in editor_csproj,
        "Unity Editor compile preflight must not include Android permission stubs",
    )
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
        "public readonly struct DynamicTensorShape",
        "public struct Input",
        "public struct Output",
        "public static class ModelLoader",
        "public sealed class Worker",
        "public sealed class Tensor<T> : Tensor where T : unmanaged",
        "[Serializable]\n    public struct TensorShape",
        "public int length",
        "public int this[int axis]",
        "public new Tensor<T> ReadbackAndClone()",
        "public void Schedule(Tensor input)",
        "DownloadToArray()",
        "GPUCompute",
    ):
        require(marker in inference_stubs, f"Unity Inference 2.2 compile stub missing reviewed API marker: {marker}")

    for runtime_text, label in (
        (detector_runtime, "PP-OCR detector runtime"),
        (recognizer_runtime, "PP-OCR recognizer runtime"),
    ):
        require(
            "worker.PeekOutput() as Tensor<float>" in runtime_text,
            f"{label} must recover the documented float output tensor from Worker.PeekOutput",
        )
        require(
            "outputTensor.DownloadToArray()" in runtime_text,
            f"{label} must use the documented synchronous Tensor<T>.DownloadToArray baseline",
        )
        require(
            "ReadbackAndClone() as Tensor<float>" not in runtime_text,
            f"{label} must not depend on a redundant readback cast in the reference synchronous path",
        )

    for marker in (
        "Compile Unity Editor guarded branches",
        "Compile Android Player guarded branches",
        "error CS\\d{4}",
        "phraselayer/unity-preflight",
        "target_url:",
        "GITHUB_RUN_ID",
    ):
        require(marker in workflow, f"Core CI missing MCP-readable Unity compiler diagnostic marker: {marker}")

    validate_asmdef(RUNTIME_ASMDEF)
    validate_asmdef(EDITOR_ASMDEF)

    if errors:
        for error in errors:
            print(f"ERROR: {error}")
        return 1

    print(
        "PASS: Unity compile preflight pins C# 9, keeps the Unity 6000 assemblies enabled, isolates Editor and Android generated sources, covers guarded branches with reviewed Inference Engine 2.2.1 signatures, and publishes exact Roslyn diagnostics before UBA"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
