#!/usr/bin/env python3
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "PhraseLayer.Core"
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
TOOLS = ROOT / "tools"
HEX40 = re.compile(r"^[0-9a-f]{40}$")
HEX64 = re.compile(r"^[0-9a-f]{64}$")
violations = []


def require(condition, message):
    if not condition:
        violations.append(message)


def require_file(path):
    if not path.is_file():
        violations.append(f"missing file: {path.relative_to(ROOT)}")


def validate_markers(path, label, markers):
    if not path.is_file():
        return
    text = path.read_text(encoding="utf-8")
    for marker in markers:
        if marker not in text:
            violations.append(f"{label} missing reviewed marker: {marker}")


for path in CORE.rglob("*.cs"):
    text = path.read_text(encoding="utf-8")
    for marker in ("using UnityEngine", "using Meta.", "using Oculus", "UnityEngine.", "OVR"):
        if marker in text:
            violations.append(f"{path.relative_to(ROOT)}: Core must not reference {marker}")

manifest_path = ROOT / "models" / "models.lock.json"
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
require(manifest.get("schema_version") == 2, "models.lock.json schema_version must be 2")

for model in manifest.get("candidates", []):
    model_id = model.get("id", "<unknown>")
    for key in ("id", "purpose", "upstream", "license", "license_status", "bundled"):
        require(key in model, f"model missing {key}: {model_id}")
    require(model.get("bundled") is False, f"model bundled too early: {model_id}")

    if not str(model.get("purpose", "")).startswith("ocr-"):
        continue

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
        require(key in model, f"OCR model missing {key}: {model_id}")

    require(isinstance(model.get("revision"), str) and HEX40.fullmatch(model["revision"]) is not None,
            f"OCR model revision must be a full 40-character Git SHA: {model_id}")
    require(model.get("format") == "onnx", f"OCR model format must remain ONNX: {model_id}")
    require(model.get("runtime_target") == "com.unity.ai.inference@2.2.1",
            f"OCR runtime target drift: {model_id}")
    require(model.get("runtime_compatibility") == "unverified-real-unity-import-required",
            f"OCR compatibility must remain unverified until real Unity import succeeds: {model_id}")
    require(model.get("quantization") == "unverified",
            f"OCR quantization must remain unverified until the ONNX graph is inspected: {model_id}")
    require(isinstance(model.get("artifact_size_bytes"), int) and model["artifact_size_bytes"] > 0,
            f"OCR primary artifact size must be pinned: {model_id}")
    require(isinstance(model.get("artifact_sha256"), str) and HEX64.fullmatch(model["artifact_sha256"]) is not None,
            f"OCR primary artifact SHA-256 must be pinned: {model_id}")

    support_artifacts = model.get("support_artifacts", [])
    require(isinstance(support_artifacts, list), f"OCR support_artifacts must be a list: {model_id}")
    if not isinstance(support_artifacts, list):
        support_artifacts = []

    seen_support_paths = set()
    for support in support_artifacts:
        require(isinstance(support, dict), f"OCR support artifact must be an object: {model_id}")
        if not isinstance(support, dict):
            continue
        for key in ("purpose", "artifact", "artifact_size_bytes", "artifact_sha256"):
            require(key in support, f"OCR support artifact missing {key}: {model_id}")
        support_path = support.get("artifact")
        require(isinstance(support_path, str) and bool(support_path),
                f"OCR support artifact path missing: {model_id}")
        if isinstance(support_path, str) and support_path:
            parsed = Path(support_path)
            require(not parsed.is_absolute() and ".." not in parsed.parts,
                    f"OCR support artifact path escapes upstream repo: {model_id}:{support_path}")
            require(support_path not in seen_support_paths,
                    f"OCR duplicate support artifact path: {model_id}:{support_path}")
            seen_support_paths.add(support_path)
        require(isinstance(support.get("artifact_size_bytes"), int) and support["artifact_size_bytes"] > 0,
                f"OCR support artifact size must be pinned: {model_id}:{support_path}")
        require(isinstance(support.get("artifact_sha256"), str) and HEX64.fullmatch(support["artifact_sha256"]) is not None,
                f"OCR support artifact SHA-256 must be pinned: {model_id}:{support_path}")

    if model.get("purpose") == "ocr-recognition":
        dictionary = model.get("recognition_dictionary")
        require(isinstance(dictionary, dict), f"OCR recognition model missing recognition_dictionary: {model_id}")
        if isinstance(dictionary, dict):
            expected = {
                "source_artifact": "inference.yml",
                "source_format": "paddle-inference-yaml",
                "postprocess_name": "CTCLabelDecode",
                "yaml_path": ["PostProcess", "character_dict"],
                "use_space_char": True,
                "raw_token_count": 6904,
                "effective_token_count": 6905,
                "generated_artifact": "ppocr_keys.txt",
                "generated_artifact_size_bytes": 27153,
                "generated_artifact_sha256": "46e1b34ef45684cb46d75ac76d355341fe7f0a2c38d6ee02e63ae6b3878019fc",
                "generated_manifest": "ppocr_keys.manifest.json",
            }
            for key, value in expected.items():
                require(dictionary.get(key) == value,
                        f"OCR recognition dictionary contract drift: {model_id} {key} expected {value!r} but found {dictionary.get(key)!r}")
            require(dictionary.get("source_artifact") in seen_support_paths,
                    f"OCR recognition dictionary source must be locked as support artifact: {model_id}")

required_files = [
    TOOLS / "stage_models.py",
    TOOLS / "extract_ppocr_dictionary.py",
    TOOLS / "test_extract_ppocr_dictionary.py",
    TOOLS / "validate_ppocr_dictionary_manifest_wiring.py",
    CORE / "PaddleOcrRuntimeContract.cs",
    CORE / "PaddleOcrDictionaryManifest.cs",
    UNITY / "ProjectSettings" / "ProjectVersion.txt",
    UNITY / "Packages" / "manifest.json",
    UNITY / "Assets" / "PhraseLayer.Unity.asmdef",
    UNITY / "Assets" / "Scripts" / "PhraseLayerDemoBehaviour.cs",
    UNITY / "Assets" / "Scripts" / "UnityTextureFramePayload.cs",
    UNITY / "Assets" / "Scripts" / "MetaPassthroughCameraBridge.cs",
    UNITY / "Assets" / "Scripts" / "OcrDebugRuntimeBehaviour.cs",
    UNITY / "Assets" / "Scripts" / "UnityInferenceModelProbe.cs",
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrDetectorRuntime.cs",
    UNITY / "Assets" / "Scripts" / "PaddleDetectorRawOutputExtensions.cs",
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrRecognizerRuntime.cs",
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrCropRectifier.cs",
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrEngine.cs",
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrDictionaryManifest.cs",
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrBootstrapBehaviour.cs",
    UNITY / "Assets" / "Resources" / "PaddleOcrPerspectiveCrop.shader",
    UNITY / "Assets" / "Editor" / "PhraseLayerEditorVerification.cs",
]
for path in required_files:
    require_file(path)

validate_markers(
    TOOLS / "stage_models.py",
    "model stager",
    ("support_artifacts", "--include-support", "artifact_kind", "PhraseLayer-model-stager/2"),
)
validate_markers(
    TOOLS / "extract_ppocr_dictionary.py",
    "PP-OCR dictionary extractor",
    (
        "paddle-inference-yaml",
        "character_dict",
        "decode_yaml_scalar",
        "generated_artifact_sha256",
        "raw token count mismatch",
        "another space token",
    ),
)
validate_markers(
    CORE / "PaddleOcrRuntimeContract.cs",
    "Core PP-OCR runtime contract",
    (
        "PaddleDbProbabilityMap.FromTensor(outputShape, outputValues)",
        "Recognizer output must be [1,time,class]",
        "dictionary token count + 1 CTC blank",
        "BuildReport(",
    ),
)
validate_markers(
    CORE / "PaddleOcrDictionaryManifest.cs",
    "Core PP-OCR dictionary manifest contract",
    (
        "ExpectedSourceArtifact = \"inference.yml\"",
        "ExpectedRawTokenCount = 6904",
        "ExpectedEffectiveTokenCount = 6905",
        "ExpectedGeneratedSha256 = \"46e1b34ef45684cb46d75ac76d355341fe7f0a2c38d6ee02e63ae6b3878019fc\"",
    ),
)
validate_markers(
    UNITY / "Assets" / "Scripts" / "OcrDebugRuntimeBehaviour.cs",
    "Unity OCR runtime driver",
    ("ConfigureEngine(IOcrEngine engine)", "ConfigureEngine(new UnityTextureOcrEngine(backend))", "new OcrFrameScheduler(engine, targetOcrHz)"),
)
validate_markers(
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrDetectorRuntime.cs",
    "Unity PP-OCR detector runtime",
    ("PHRASELAYER_UNITY_AI_INFERENCE_2_2", "PaddleOcrV6TinyDetectionPreprocess.CreateResizeTransform", "worker.Schedule(inputTensor)", "ReadbackAndClone()"),
)
validate_markers(
    UNITY / "Assets" / "Scripts" / "PaddleDetectorRawOutputExtensions.cs",
    "Unity PP-OCR DB decoder",
    ("PaddleDbProbabilityMap.FromTensor", "PaddleDbQuadPostprocessor", "output.ResizeTransform.SourceWidth", "output.ResizeTransform.SourceHeight"),
)
validate_markers(
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrRecognizerRuntime.cs",
    "Unity PP-OCR recognizer runtime",
    ("PaddleOcrV6TinyRecognitionPreprocess.CreateResizeTransform", "PaddleCtcGreedyDecoder.DecodeFromPredictions", "worker.Schedule(inputTensor)", "ReadbackAndClone()"),
)
validate_markers(
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrCropRectifier.cs",
    "Unity PP-OCR crop rectifier",
    ("PaddleOcrCropRectification.CreatePlan", "ProjectiveTransformFactory.UnitSquareToQuad", "Graphics.Blit(source, target, material, 0)", "RenderTextureFormat.ARGB32"),
)
validate_markers(
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrEngine.cs",
    "Unity PP-OCR end-to-end engine",
    (
        "detector.Execute(texture, frame.Width, frame.Height)",
        "PaddleOcrRuntimeContract.ValidateDetector",
        "DecodeV6TinyQuads(dbSpec)",
        "PaddleOcrReadingOrder.Sort",
        "cropRectifier.Rectify(texture, detection.ImageBounds)",
        "PaddleOcrRuntimeContract.ValidateRecognizer",
        "recognizerOutput.Decode(characterDictionary)",
        "RuntimeContractReport",
        "PaddleOcrObservationAssembler.Assemble",
        "ValidateRecognizerConfidence",
    ),
)
validate_markers(
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrDictionaryManifest.cs",
    "Unity PP-OCR dictionary manifest",
    ("JsonUtility.FromJson<ManifestJson>", "dictionaryAsset.bytes", "SHA256.Create()", "PaddleOcrDictionaryManifestContract.ValidateAndBuildReport"),
)
validate_markers(
    UNITY / "Assets" / "Scripts" / "UnityPaddleOcrBootstrapBehaviour.cs",
    "Unity PP-OCR scene bootstrap",
    (
        "characterDictionaryManifest",
        "UnityPaddleOcrDictionaryManifest.Validate(",
        "PaddleOcrCharacterDictionary.Parse(characterDictionary.text, useSpaceCharacter)",
        "new UnityPaddleOcrEngine(",
        "runtimeDriver.ConfigureEngine(created)",
        "DictionaryManifestReport",
        "RuntimeContractReport",
    ),
)
validate_markers(
    UNITY / "Assets" / "Resources" / "PaddleOcrPerspectiveCrop.shader",
    "Unity PP-OCR crop shader",
    ("_RotateCCW90", "preRotationTop = float2(1.0 - outputTop.y, outputTop.x)", "sourceUv = saturate(sourceUv)", "tex2D(_MainTex, sourceUv)"),
)

core_package = json.loads((CORE / "package.json").read_text(encoding="utf-8"))
require(core_package.get("name") == "com.unjuno.phraselayer.core", "unexpected Core UPM package name")
core_asmdef = json.loads((CORE / "PhraseLayer.Core.asmdef").read_text(encoding="utf-8"))
require(core_asmdef.get("noEngineReferences") is True, "PhraseLayer.Core.asmdef must set noEngineReferences=true")

unity_asmdef = json.loads((UNITY / "Assets" / "PhraseLayer.Unity.asmdef").read_text(encoding="utf-8"))
require("Unity.InferenceEngine" in unity_asmdef.get("references", []), "PhraseLayer.Unity.asmdef must reference Unity.InferenceEngine")
expected_define = {
    "name": "com.unity.ai.inference",
    "expression": "[2.2.1,2.3.0)",
    "define": "PHRASELAYER_UNITY_AI_INFERENCE_2_2",
}
require(expected_define in unity_asmdef.get("versionDefines", []),
        "PhraseLayer.Unity.asmdef must gate the reviewed Unity Inference 2.2.x API surface")

unity_manifest = json.loads((UNITY / "Packages" / "manifest.json").read_text(encoding="utf-8"))
deps = unity_manifest.get("dependencies", {})
for package, expected in {
    "com.unjuno.phraselayer.core": "file:../../src/PhraseLayer.Core",
    "com.meta.xr.mrutilitykit": "85.0.0",
    "com.unity.ai.inference": "2.2.1",
    "com.unity.xr.management": "4.5.4",
    "com.unity.xr.meta-openxr": "2.2.1",
    "com.unity.xr.openxr": "1.15.1",
    "com.unity.ugui": "2.0.0",
}.items():
    require(deps.get(package) == expected,
            f"Unity package drift: {package} expected {expected} but found {deps.get(package)}")

project_version = (UNITY / "ProjectSettings" / "ProjectVersion.txt").read_text(encoding="utf-8")
require("m_EditorVersion: 6000.0.66f2" in project_version,
        "Unity editor pin must remain 6000.0.66f2 until the Meta baseline is intentionally updated")

if violations:
    raise SystemExit("\n".join(violations))

print(
    f"PASS: {len(list(CORE.rglob('*.cs')))} core files; Core/Unity boundaries, fully pinned OCR artifacts, "
    "measured PP-OCR YAML dictionary contract, Meta/Unity package pins, and end-to-end OCR runtime markers validated"
)
