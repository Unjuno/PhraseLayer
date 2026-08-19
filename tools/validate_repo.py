#!/usr/bin/env python3
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "PhraseLayer.Core"
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
forbidden = ("using UnityEngine", "using Meta.", "using Oculus", "UnityEngine.", "OVR")
violations=[]

for path in CORE.rglob("*.cs"):
    text=path.read_text(encoding="utf-8")
    for marker in forbidden:
        if marker in text:
            violations.append(f"{path.relative_to(ROOT)}: {marker}")

manifest=json.loads((ROOT/"models"/"models.lock.json").read_text(encoding="utf-8"))
if manifest.get("schema_version") != 2:
    violations.append("models.lock.json schema_version must be 2")

for model in manifest["candidates"]:
    if model.get("bundled") is not False:
        violations.append(f"model bundled too early: {model.get('id')}")
    for key in ("id","purpose","upstream","license","license_status","bundled"):
        if key not in model:
            violations.append(f"model missing {key}: {model}")

    if str(model.get("purpose", "")).startswith("ocr-"):
        for key in (
            "revision",
            "artifact",
            "artifact_size_bytes",
            "artifact_sha256",
            "format",
            "runtime_target",
            "runtime_compatibility",
            "quantization",
            "source_precision",
        ):
            if key not in model:
                violations.append(f"OCR model missing {key}: {model.get('id')}")

        revision = model.get("revision")
        if not isinstance(revision, str) or re.fullmatch(r"[0-9a-f]{40}", revision) is None:
            violations.append(f"OCR model revision must be a full 40-character Git SHA: {model.get('id')}")
        if model.get("format") != "onnx":
            violations.append(f"OCR model format must remain explicit ONNX: {model.get('id')}")
        if model.get("runtime_target") != "com.unity.ai.inference@2.2.1":
            violations.append(f"OCR runtime target drift: {model.get('id')}")
        if model.get("runtime_compatibility") != "unverified-real-unity-import-required":
            violations.append(f"OCR compatibility must remain unverified until real Unity import succeeds: {model.get('id')}")
        if model.get("quantization") != "unverified":
            violations.append(f"OCR quantization must remain unverified until the ONNX graph is inspected: {model.get('id')}")

        artifact_sha = model.get("artifact_sha256")
        if artifact_sha is not None and (not isinstance(artifact_sha, str) or re.fullmatch(r"[0-9a-f]{64}", artifact_sha) is None):
            violations.append(f"OCR artifact SHA-256 must be null or 64 lowercase hex characters: {model.get('id')}")
        artifact_size = model.get("artifact_size_bytes")
        if artifact_size is not None and (not isinstance(artifact_size, int) or artifact_size <= 0):
            violations.append(f"OCR artifact size must be null or a positive integer: {model.get('id')}")

required_unity = [
    UNITY / "ProjectSettings" / "ProjectVersion.txt",
    UNITY / "Packages" / "manifest.json",
    UNITY / "Assets" / "PhraseLayer.Unity.asmdef",
    UNITY / "Assets" / "Scripts" / "PhraseLayerDemoBehaviour.cs",
    UNITY / "Assets" / "Scripts" / "UnityTextureFramePayload.cs",
    UNITY / "Assets" / "Scripts" / "MetaPassthroughCameraBridge.cs",
    UNITY / "Assets" / "Scripts" / "UnityInferenceModelProbe.cs",
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrDetectorRuntime.cs",
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrRecognizerRuntime.cs",
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrCropRectifier.cs",
    UNITY / "Assets" / "Resources" / "PaddleOcrPerspectiveCrop.shader",
    UNITY / "Assets" / "Editor" / "PhraseLayerEditorVerification.cs",
]
for path in required_unity:
    if not path.exists():
        violations.append(f"missing Unity shell file: {path.relative_to(ROOT)}")

def validate_runtime(path, label, markers):
    if not path.exists():
        return
    text = path.read_text(encoding="utf-8")
    for marker in markers:
        if marker not in text:
            violations.append(f"{label} missing reviewed marker: {marker}")

validate_runtime(
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrDetectorRuntime.cs",
    "Unity PP-OCR detector runtime",
    (
        "PHRASELAYER_UNITY_AI_INFERENCE_2_2",
        "PaddleOcrV6TinyDetectionPreprocess.CreateResizeTransform",
        "new Tensor<float>",
        "worker.Schedule(inputTensor)",
        "worker.PeekOutput() as Tensor<float>",
        "ReadbackAndClone()",
    ),
)

validate_runtime(
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrRecognizerRuntime.cs",
    "Unity PP-OCR recognizer runtime",
    (
        "PHRASELAYER_UNITY_AI_INFERENCE_2_2",
        "PaddleOcrV6TinyRecognitionPreprocess.CreateResizeTransform",
        "PaddleCtcGreedyDecoder.DecodeFromPredictions",
        "new Tensor<float>",
        "worker.Schedule(inputTensor)",
        "worker.PeekOutput() as Tensor<float>",
        "ReadbackAndClone()",
    ),
)

validate_runtime(
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrCropRectifier.cs",
    "Unity PP-OCR crop rectifier",
    (
        "PaddleOcrCropRectification.CreatePlan",
        "ProjectiveTransformFactory.UnitSquareToQuad",
        "Resources.Load<Shader>(ShaderResourceName)",
        "Graphics.Blit(source, target, material, 0)",
        "RenderTextureFormat.ARGB32",
    ),
)

validate_runtime(
    UNITY / "Assets" / "Resources" / "PaddleOcrPerspectiveCrop.shader",
    "Unity PP-OCR crop shader",
    (
        "_RotateCCW90",
        "preRotationTop = float2(1.0 - outputTop.y, outputTop.x)",
        "sourceUv = saturate(sourceUv)",
        "tex2D(_MainTex, sourceUv)",
    ),
)

core_package=json.loads((CORE/"package.json").read_text(encoding="utf-8"))
if core_package.get("name") != "com.unjuno.phraselayer.core":
    violations.append("unexpected Core UPM package name")

core_asmdef=json.loads((CORE/"PhraseLayer.Core.asmdef").read_text(encoding="utf-8"))
if core_asmdef.get("noEngineReferences") is not True:
    violations.append("PhraseLayer.Core.asmdef must set noEngineReferences=true")

unity_asmdef=json.loads((UNITY/"Assets"/"PhraseLayer.Unity.asmdef").read_text(encoding="utf-8"))
if "Unity.InferenceEngine" not in unity_asmdef.get("references", []):
    violations.append("PhraseLayer.Unity.asmdef must reference Unity.InferenceEngine")
expected_inference_define = {
    "name": "com.unity.ai.inference",
    "expression": "[2.2.1,2.3.0)",
    "define": "PHRASELAYER_UNITY_AI_INFERENCE_2_2",
}
if expected_inference_define not in unity_asmdef.get("versionDefines", []):
    violations.append("PhraseLayer.Unity.asmdef must gate the reviewed Unity Inference 2.2.x API surface")

unity_manifest=json.loads((UNITY/"Packages"/"manifest.json").read_text(encoding="utf-8"))
deps=unity_manifest.get("dependencies", {})
expected_packages = {
    "com.unjuno.phraselayer.core": "file:../../src/PhraseLayer.Core",
    "com.meta.xr.mrutilitykit": "85.0.0",
    "com.unity.ai.inference": "2.2.1",
    "com.unity.xr.management": "4.5.4",
    "com.unity.xr.openxr": "1.15.1",
    "com.unity.ugui": "2.0.0",
}
for package, expected in expected_packages.items():
    actual=deps.get(package)
    if actual != expected:
        violations.append(f"Unity package drift: {package} expected {expected} but found {actual}")

project_version=(UNITY/"ProjectSettings"/"ProjectVersion.txt").read_text(encoding="utf-8")
if "m_EditorVersion: 6000.0.66f2" not in project_version:
    violations.append("Unity editor pin must remain 6000.0.66f2 until the Meta baseline is intentionally updated")

if violations:
    raise SystemExit("\n".join(violations))

print(
    f"PASS: {len(list(CORE.rglob('*.cs')))} core files; boundaries, model manifest, "
    "Unity shell, Meta baseline package pins, camera adapter structure, Inference 2.2 API gate, "
    "and PP-OCR detector/recognizer/crop runtime markers validated"
)
