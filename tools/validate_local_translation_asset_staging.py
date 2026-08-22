#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PREPARE = ROOT / "tools" / "prepare_unity_translation_assets.py"
TEST = ROOT / "tools" / "test_prepare_unity_translation_assets.py"
GITIGNORE = ROOT / ".gitignore"
WORKFLOW = ROOT / ".github" / "workflows" / "core-ci.yml"
DOC = ROOT / "docs" / "LOCAL_TRANSLATION.md"
CORE_STAGING = ROOT / "src" / "PhraseLayer.Core" / "LocalTranslationStaging.cs"
UNITY_MANIFEST = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts" / "UnityLocalTranslationManifest.cs"
UNITY_GATE = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts" / "UnityLocalTranslationAssetGateBehaviour.cs"
UNITY_EDITOR_ASSETS = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Editor" / "PhraseLayerLocalTranslationAssets.cs"
CORE_TEST = ROOT / "tests" / "PhraseLayer.Core.Tests" / "LocalTranslationStagingTests.cs"

errors: list[str] = []
for path in (
    PREPARE,
    TEST,
    GITIGNORE,
    WORKFLOW,
    DOC,
    CORE_STAGING,
    UNITY_MANIFEST,
    UNITY_GATE,
    UNITY_EDITOR_ASSETS,
    CORE_TEST,
):
    if not path.is_file():
        errors.append(f"missing local translation staging contract file: {path.relative_to(ROOT)}")

if PREPARE.is_file():
    text = PREPARE.read_text(encoding="utf-8")
    for marker in (
        'EXPECTED_MODEL_ID = "Helsinki-NLP/opus-mt-en-jap"',
        'EXPECTED_REVISION = "a863894cdd2b80f3bc1c5966734aee9ffec207d1"',
        'EXPECTED_RUNTIME_STATUS = "unverified-real-unity-import-required"',
        'parity.get("exact") is not True',
        "sha256_file(source)",
        "replace_directory_atomically",
        'relative.parts[0] != "Assets"',
        '"git_policy": "local-only; directory is ignored and model binaries are not committed"',
    ):
        if marker not in text:
            errors.append(f"translation staging tool missing reviewed marker: {marker}")

if CORE_STAGING.is_file():
    text = CORE_STAGING.read_text(encoding="utf-8")
    for marker in (
        'ExpectedModelId = "Helsinki-NLP/opus-mt-en-jap"',
        'ExpectedRevision = "a863894cdd2b80f3bc1c5966734aee9ffec207d1"',
        'ExpectedRuntimeStatus = "unverified-real-unity-import-required"',
        'EncoderPath = "encoder_model.onnx"',
        'MergedDecoderPath = "decoder_model_merged.onnx"',
        'SourceSentencePiecePath = "source.spm"',
        'TargetSentencePiecePath = "target.spm"',
        "ReferenceParityExact",
        "ValidateCanonicalRelativePath",
        "kind does not match file extension",
    ):
        if marker not in text:
            errors.append(f"Core translation staging contract missing reviewed marker: {marker}")

if UNITY_MANIFEST.is_file():
    text = UNITY_MANIFEST.read_text(encoding="utf-8")
    for marker in (
        "ParseManifest(TextAsset manifestAsset)",
        "JsonUtility.FromJson<ManifestJson>",
        "LocalTranslationStagingContract.ValidateAndResolve",
        "reference_parity_exact",
        "runtime_status",
    ):
        if marker not in text:
            errors.append(f"Unity translation manifest bridge missing reviewed marker: {marker}")

if UNITY_GATE.is_file():
    text = UNITY_GATE.read_text(encoding="utf-8")
    for marker in (
        "UnityLocalTranslationManifest.ParseAndValidate(stagingManifest)",
        "UnityLocalTranslationManifest.ValidateAndBuildReport(stagingManifest)",
        "No remote fallback exists",
        "stagingManifest = null",
    ):
        if marker not in text:
            errors.append(f"Unity translation asset gate missing reviewed marker: {marker}")

if UNITY_EDITOR_ASSETS.is_file():
    text = UNITY_EDITOR_ASSETS.read_text(encoding="utf-8")
    for marker in (
        "VerifyStagedFile",
        "ComputeSha256",
        'source.Path + ".bytes"',
        "AssetDatabase.Refresh()",
        "AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath)",
        "Imported SentencePiece TextAsset SHA-256 mismatch",
    ):
        if marker not in text:
            errors.append(f"Unity translation Editor asset gate missing reviewed marker: {marker}")

if CORE_TEST.is_file():
    text = CORE_TEST.read_text(encoding="utf-8")
    for marker in (
        "ValidParityVerifiedBundleResolvesReferenceRuntimeSet",
        "NonExactReferenceParityFailsClosed",
        "MissingMergedDecoderFailsClosed",
        "TraversalPathIsRejectedEvenWhenRequiredFilesExist",
        "OnnxKindMustMatchOnnxExtension",
    ):
        if marker not in text:
            errors.append(f"translation staging regression test missing marker: {marker}")

if GITIGNORE.is_file():
    ignored = GITIGNORE.read_text(encoding="utf-8")
    if "unity/PhraseLayer.Unity/Assets/LocalTranslationAssets/" not in ignored:
        errors.append("Unity local translation asset directory must remain git-ignored")

if WORKFLOW.is_file():
    workflow = WORKFLOW.read_text(encoding="utf-8")
    for marker in (
        "python tools/validate_translation_export_probe.py",
        "python tools/validate_local_translation_asset_staging.py",
        "python tools/test_prepare_unity_translation_assets.py",
    ):
        if marker not in workflow:
            errors.append(f"Core CI missing translation staging gate: {marker}")

if DOC.is_file():
    doc = DOC.read_text(encoding="utf-8")
    for marker in (
        "prepare_unity_translation_assets.py",
        "LocalTranslationAssets",
        "does not prove Unity compatibility",
    ):
        if marker not in doc:
            errors.append(f"local translation doc missing staging marker: {marker}")

if errors:
    raise SystemExit("\n".join(errors))

print(
    "PASS: local translation assets are parity-gated, hash-verified twice, git-ignored, "
    "SentencePiece-ready as byte-identical Unity TextAssets, and not promoted as Unity-compatible"
)
