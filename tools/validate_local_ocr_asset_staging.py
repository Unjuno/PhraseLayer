#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
GITIGNORE = ROOT / ".gitignore"
TOOLS = ROOT / "tools"
UNITY_ASSETS = ROOT / "unity" / "PhraseLayer.Unity" / "Assets"
EDITOR = UNITY_ASSETS / "Editor"
SCRIPTS = UNITY_ASSETS / "Scripts"

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

runtime_config = require_file(SCRIPTS / "UnityLocalOcrRuntimeConfig.cs")
for marker in (
    'ResourcesName = "PhraseLayerLocalOcrRuntimeConfig"',
    "public sealed class UnityLocalOcrRuntimeConfig : ScriptableObject",
    "public bool IsConfigured =>",
    "detectorModel != null",
    "recognizerModel != null",
    "characterDictionary != null",
    "characterDictionaryManifest != null",
    "bootstrap.Configure(",
    "BackendType.GPUCompute",
    "never downloads model weights",
):
    if marker not in runtime_config:
        violations.append(f"local PP-OCR Player runtime config missing reviewed marker: {marker}")

runtime_builder = require_file(EDITOR / "PhraseLayerLocalOcrRuntimeConfigBuilder.cs")
for marker in (
    'RuntimeResourcesDirectory = PhraseLayerLocalOcrAssets.Root + "/Resources"',
    'RuntimeConfigAssetPath = RuntimeResourcesDirectory + "/PhraseLayerLocalOcrRuntimeConfig.asset"',
    "PhraseLayerLocalOcrAssets.VerifyLocalAssets();",
    "UnityPaddleOcrDictionaryManifest.Validate(",
    "HasAnyStagedLocalOcrFile()",
    "HasCompleteStagedLocalOcrBundle()",
    "PhraseLayerLocalOcrRuntimeConfigBuildHook : IPreprocessBuildWithReport",
    "public int callbackOrder => -1500",
    "local PP-OCR assets are absent; Player will keep the synthetic OCR fallback",
    "partial local PP-OCR bundle",
    "PrepareRuntimeConfigAsset();",
):
    if marker not in runtime_builder:
        violations.append(f"local PP-OCR Player config builder missing reviewed marker: {marker}")
for forbidden in ("urllib", "urlopen", "requests.", "http://", "https://"):
    if forbidden in runtime_builder:
        violations.append(f"local PP-OCR Player config builder must not perform network access: {forbidden}")

installer = require_file(SCRIPTS / "PhraseLayerReadMvpRuntimeInstaller.cs")
for marker in (
    "Resources.Load<UnityLocalOcrRuntimeConfig>(UnityLocalOcrRuntimeConfig.ResourcesName)",
    "var useLocalOcr = localOcrConfig != null && localOcrConfig.IsConfigured",
    "SetGameObjectActive(root, false);",
    "root.AddComponent<UnityPaddleOcrBootstrapBehaviour>()",
    "presenter.LoadSyntheticFixtureOnStart = !useLocalOcr",
    "runtimeDriver.AutoRun = useLocalOcr",
    "localOcrConfig.ConfigureBootstrap(ocrBootstrap, runtimeDriver)",
    "SetGameObjectActive(root, true);",
    'useLocalOcr ? "local-ppocr-camera" : "synthetic-fixture"',
):
    if marker not in installer:
        violations.append(f"committed Read MVP local OCR startup missing reviewed marker: {marker}")

bootstrap = require_file(SCRIPTS / "UnityPaddleOcrBootstrapBehaviour.cs")
for marker in (
    "public bool HasConfiguredAssets =>",
    "public void Configure(",
    "Cannot reconfigure PP-OCR assets after the engine has initialized",
    "runtimeDriver.ConfigureEngine(created);",
):
    if marker not in bootstrap:
        violations.append(f"PP-OCR bootstrap runtime configuration missing reviewed marker: {marker}")

if violations:
    raise SystemExit("\n".join(violations))

print(
    "PASS: local PP-OCR assets remain git-ignored and offline-verified; a complete staged bundle is packaged through "
    "a local Resources config and auto-starts real camera OCR in the committed Read MVP, while absent assets retain "
    "the deterministic synthetic fallback and partial bundles fail closed before Player build"
)
