#!/usr/bin/env python3
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
GITIGNORE = ROOT / ".gitignore"
TOOLS = ROOT / "tools"
EDITOR = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Editor"
EDITOR_ASMDEF = EDITOR / "PhraseLayer.Unity.Editor.asmdef"

violations = []


def require_file(path: Path) -> str:
    if not path.is_file():
        violations.append(f"missing file: {path.relative_to(ROOT)}")
        return ""
    return path.read_text(encoding="utf-8")


gitignore = require_file(GITIGNORE)
if "unity/PhraseLayer.Unity/Assets/LocalOcrAssets/" not in gitignore:
    violations.append(".gitignore must exclude Unity Assets/LocalOcrAssets so model/dictionary bytes remain local-only")

stager = require_file(TOOLS / "prepare_unity_ocr_assets.py")
for marker in (
    "Assets/LocalOcrAssets/PaddleOCR",
    "verify_file_against_artifact",
    "extract_ppocr_dictionary.export_dictionary",
    "copy_and_verify",
    "PhraseLayerOcrAssets.manifest.json",
    "Unity local asset path must live under the project's Assets directory",
):
    if marker not in stager:
        violations.append(f"local Unity OCR stager missing reviewed marker: {marker}")
for forbidden in ("urllib", "urlopen", "requests.", "http://", "https://"):
    if forbidden in stager:
        violations.append(f"local Unity OCR stager must not perform network access: {forbidden}")

fixture = require_file(TOOLS / "test_prepare_unity_ocr_assets.py")
for marker in (
    "test_prepare_verifies_generates_and_copies_local_assets",
    "test_prepare_rejects_corrupted_staged_primary_artifact",
    "test_prepare_rejects_destination_outside_assets",
    "test_prepare_rejects_parent_traversal",
):
    if marker not in fixture:
        violations.append(f"local Unity OCR staging fixture missing reviewed test: {marker}")

editor_asmdef_text = require_file(EDITOR_ASMDEF)
if editor_asmdef_text:
    try:
        editor_asmdef = json.loads(editor_asmdef_text)
    except json.JSONDecodeError as exception:
        violations.append(f"Unity Editor asmdef is invalid JSON: {exception}")
    else:
        if "Unity.InferenceEngine" not in editor_asmdef.get("references", []):
            violations.append("Unity Editor asmdef must reference Unity.InferenceEngine for local OCR probes")
        expected_version_define = {
            "name": "com.unity.ai.inference",
            "expression": "[2.2.1,2.3.0)",
            "define": "PHRASELAYER_UNITY_AI_INFERENCE_2_2",
        }
        if expected_version_define not in editor_asmdef.get("versionDefines", []):
            violations.append(
                "Unity Editor asmdef must mirror the runtime com.unity.ai.inference 2.2.x version define"
            )

editor = require_file(EDITOR / "PhraseLayerLocalOcrAssets.cs")
for marker in (
    'Root = "Assets/LocalOcrAssets/PaddleOCR"',
    'DetectorPath = Root + "/detector.onnx"',
    'RecognizerPath = Root + "/recognizer.onnx"',
    'DictionaryPath = Root + "/ppocr_keys.txt"',
    'DictionaryManifestPath = Root + "/ppocr_keys.manifest.json"',
    "UnityPaddleOcrDictionaryManifest.Validate(",
    "UnityInferenceModelProbe.BuildReport(detector)",
    "UnityInferenceModelProbe.BuildReport(recognizer)",
    "VerifyLocalAssetsBatch",
    "RunLocalInferenceProbe",
    "RunLocalInferenceProbeBatch",
    "SyntheticProbeSize = 256",
    "new UnityPaddleOcrDetectorRuntime(",
    "BackendType.GPUCompute",
    "PaddleOcrRuntimeContract.ValidateDetector(",
    "new UnityPaddleOcrRecognizerRuntime(",
    "PaddleOcrRuntimeContract.ValidateRecognizer(",
    "PaddleOcrDictionaryManifestContract.ExpectedEffectiveTokenCount",
    "ValidateUnitInterval(",
    "must be probabilistic in [0,1]",
    "do not run this gate with -nographics",
    "EditorApplication.Exit(0)",
    "EditorApplication.Exit(1)",
    "AssignLocalAssetsToSceneBootstrap",
    'RequireProperty(serialized, "characterDictionaryManifest")',
    'RequireProperty(serialized, "useSpaceCharacter").boolValue = true',
    "Verified local PP-OCR assets were already assigned",
):
    if marker not in editor:
        violations.append(f"Unity local PP-OCR Editor bridge missing reviewed marker: {marker}")

if violations:
    raise SystemExit("\n".join(violations))

print(
    "PASS: local PP-OCR assets are git-ignored, offline-verified, batch-importable, synthetic-inference-probeable, "
    "and the Editor assembly mirrors the reviewed Inference Engine 2.2.x gate"
)
