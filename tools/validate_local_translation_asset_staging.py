#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PREPARE = ROOT / "tools" / "prepare_unity_translation_assets.py"
TEST = ROOT / "tools" / "test_prepare_unity_translation_assets.py"
FIXTURE_PREPARE = ROOT / "tools" / "prepare_translation_tokenizer_fixture_manifest.py"
FIXTURE_TEST = ROOT / "tools" / "test_prepare_translation_tokenizer_fixture_manifest.py"
GITIGNORE = ROOT / ".gitignore"
WORKFLOW = ROOT / ".github" / "workflows" / "core-ci.yml"
DOC = ROOT / "docs" / "LOCAL_TRANSLATION.md"
CORE_STAGING = ROOT / "src" / "PhraseLayer.Core" / "LocalTranslationStaging.cs"
CORE_FIXTURES = ROOT / "src" / "PhraseLayer.Core" / "TranslationTokenizerFixtureManifest.cs"
CORE_ONNX = ROOT / "src" / "PhraseLayer.Core" / "OpusMtOnnxExportMetadata.cs"
UNITY_MANIFEST = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts" / "UnityLocalTranslationManifest.cs"
UNITY_GATE = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts" / "UnityLocalTranslationAssetGateBehaviour.cs"
UNITY_BOOTSTRAP = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts" / "UnityLocalTranslationBootstrapBehaviour.cs"
UNITY_MODEL_PROBE = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts" / "UnityOpusMtModelProbe.cs"
UNITY_EDITOR_ASSETS = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Editor" / "PhraseLayerLocalTranslationAssets.cs"
CORE_TEST = ROOT / "tests" / "PhraseLayer.Core.Tests" / "LocalTranslationStagingTests.cs"
BOOTSTRAP_TEST = ROOT / "tests" / "PhraseLayer.Core.Tests" / "LocalTranslationBootstrapStagingTests.cs"
FIXTURE_CORE_TEST = ROOT / "tests" / "PhraseLayer.Core.Tests" / "TranslationTokenizerFixtureManifestTests.cs"
ONNX_TEST = ROOT / "tests" / "PhraseLayer.Core.Tests" / "OpusMtOnnxExportMetadataTests.cs"

errors: list[str] = []
for path in (
    PREPARE,
    TEST,
    FIXTURE_PREPARE,
    FIXTURE_TEST,
    GITIGNORE,
    WORKFLOW,
    DOC,
    CORE_STAGING,
    CORE_FIXTURES,
    CORE_ONNX,
    UNITY_MANIFEST,
    UNITY_GATE,
    UNITY_BOOTSTRAP,
    UNITY_MODEL_PROBE,
    UNITY_EDITOR_ASSETS,
    CORE_TEST,
    BOOTSTRAP_TEST,
    FIXTURE_CORE_TEST,
    ONNX_TEST,
):
    if not path.is_file():
        errors.append(f"missing local translation staging contract file: {path.relative_to(ROOT)}")

if PREPARE.is_file():
    text = PREPARE.read_text(encoding="utf-8")
    for marker in (
        'EXPECTED_MODEL_ID = "Helsinki-NLP/opus-mt-en-jap"',
        'EXPECTED_REVISION = "a863894cdd2b80f3bc1c5966734aee9ffec207d1"',
        'EXPECTED_RUNTIME_STATUS = "unverified-real-unity-import-required"',
        'MANAGED_TOKENIZER_NAME = "phraselayer-sentencepiece-unigram-v1.txt"',
        'TOKENIZER_FIXTURE_NAME = "phraselayer-tokenizer-fixtures-v1.txt"',
        'parity.get("exact") is not True',
        "sha256_file(source)",
        "replace_directory_atomically",
        "copy_generated",
        '"kind": "generated"',
        '"runtime_bootstrap_ready": bootstrap_ready',
        'relative.parts[0] != "Assets"',
        '"git_policy": "local-only; directory is ignored and model binaries are not committed"',
    ):
        if marker not in text:
            errors.append(f"translation staging tool missing reviewed marker: {marker}")

if FIXTURE_PREPARE.is_file():
    text = FIXTURE_PREPARE.read_text(encoding="utf-8")
    for marker in (
        'MAGIC = "PHRASELAYER_TRANSLATION_TOKENIZER_FIXTURES_V1"',
        'managed_tokenizer_parity',
        'strip_decoder_control_tokens',
        '"uploaded": False',
    ):
        if marker not in text:
            errors.append(f"tokenizer fixture manifest generator missing reviewed marker: {marker}")

if CORE_STAGING.is_file():
    text = CORE_STAGING.read_text(encoding="utf-8")
    for marker in (
        'ExpectedModelId = "Helsinki-NLP/opus-mt-en-jap"',
        'ExpectedRevision = "a863894cdd2b80f3bc1c5966734aee9ffec207d1"',
        'ExpectedRuntimeStatus = "unverified-real-unity-import-required"',
        'EncoderPath = "encoder_model.onnx"',
        'DecoderPath = "decoder_model.onnx"',
        'SourceSentencePiecePath = "source.spm"',
        'TargetSentencePiecePath = "target.spm"',
        'ManagedTokenizerManifestPath = "phraselayer-sentencepiece-unigram-v1.txt"',
        'TokenizerFixtureManifestPath = "phraselayer-tokenizer-fixtures-v1.txt"',
        "ValidateAndResolveBootstrapArtifacts",
        'Require(byPath, ManagedTokenizerManifestPath, "generated")',
        'Require(byPath, TokenizerFixtureManifestPath, "generated")',
        "ReferenceParityExact",
        "ValidateCanonicalRelativePath",
        "kind does not match file extension",
        "runtime.Decoder.Path",
    ):
        if marker not in text:
            errors.append(f"Core translation staging contract missing reviewed marker: {marker}")
    if "decoder_model_merged.onnx" in text:
        errors.append("correctness-first staging contract must not require the cache-heavy merged decoder")

if CORE_FIXTURES.is_file():
    text = CORE_FIXTURES.read_text(encoding="utf-8")
    for marker in (
        'Magic = "PHRASELAYER_TRANSLATION_TOKENIZER_FIXTURES_V1"',
        "TranslationTokenizerFixtureSet Parse",
        "LocalTranslationStagingContract.ExpectedModelId",
        "LocalTranslationStagingContract.ExpectedRevision",
        "DecodeBase64Utf8",
    ):
        if marker not in text:
            errors.append(f"Core tokenizer fixture manifest parser missing reviewed marker: {marker}")

if CORE_ONNX.is_file():
    text = CORE_ONNX.read_text(encoding="utf-8")
    for marker in (
        'ProbeCommit = "792055c78981de4dfaf2a4b38865793005a546cb"',
        "ReferenceRuntimeSizeBytes = 463431659",
        "HiddenSize = 512",
        "VocabularySize = 46276",
        '"encoder_model.onnx"',
        '"decoder_model.onnx"',
        '"bb0d8d22053062bbd3695a468c88d1f84367eb195fa5f9fb75aa6c9548f57c59"',
        '"513bbf05f48da69847ce247e3245a5e84a814a7e591e8f544dea4854d202dc00"',
        '"encoder_hidden_states"',
        '"logits"',
        "opset: 18",
    ):
        if marker not in text:
            errors.append(f"measured OPUS-MT ONNX contract missing marker: {marker}")

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
        "LocalTranslationStagingContract.ValidateAndResolve(manifest)",
        "LocalTranslationStagingContract.ValidateAndResolveBootstrapArtifacts(manifest)",
        "ValidateBootstrapAssets(",
        "VerifyTextAsset",
        "ComputeSha256",
        "bootstrap_artifacts=verified",
        "No remote fallback exists",
        "stagingManifest = null",
    ):
        if marker not in text:
            errors.append(f"Unity translation asset gate missing reviewed marker: {marker}")

if UNITY_BOOTSTRAP.is_file():
    text = UNITY_BOOTSTRAP.read_text(encoding="utf-8")
    for marker in (
        "assetGate.ValidateBootstrapAssets(",
        "ManagedSentencePieceManifest.ParseTokenizer",
        "TranslationTokenizerFixtureManifest.Parse",
        "UnityOpusMtModelProbe.ValidateAndBuildReport",
        "new UnityOpusMtAutoregressiveBackend(",
        "OpusMtEnJapLocalEngineFactory.CreateReferenceEngine(",
        "readAssistance.ConfigureTranslationEngine(engine)",
        "candidateBackend?.Dispose()",
        "No network or remote fallback exists",
    ):
        if marker not in text:
            errors.append(f"Unity local translation bootstrap missing reviewed marker: {marker}")

if UNITY_MODEL_PROBE.is_file():
    text = UNITY_MODEL_PROBE.read_text(encoding="utf-8")
    for marker in (
        "UnityOpusMtModelProbe.ValidateAndBuildReport",
        'new[] { "input_ids", "attention_mask" }',
        'new[] { "encoder_attention_mask", "input_ids", "encoder_hidden_states" }',
        'RequireOutput(encoder, "encoder", "last_hidden_state")',
        'RequireOutput(decoder, "decoder", "logits")',
        "runtime-execution=unverified quest=unverified",
    ):
        if marker not in text:
            errors.append(f"Unity OPUS-MT import probe missing reviewed marker: {marker}")

if UNITY_EDITOR_ASSETS.is_file():
    text = UNITY_EDITOR_ASSETS.read_text(encoding="utf-8")
    for marker in (
        "VerifyStagedFile",
        "ComputeSha256",
        'sourceAssetPath + ".bytes"',
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
        "MissingReferenceDecoderFailsClosed",
        "TraversalPathIsRejectedEvenWhenRequiredFilesExist",
        "OnnxKindMustMatchOnnxExtension",
    ):
        if marker not in text:
            errors.append(f"translation staging regression test missing marker: {marker}")

if BOOTSTRAP_TEST.is_file():
    text = BOOTSTRAP_TEST.read_text(encoding="utf-8")
    for marker in (
        "GeneratedTokenizerArtifactsResolveOnlyWhenBothArePresent",
        "MissingTokenizerFixturesFailsClosed",
    ):
        if marker not in text:
            errors.append(f"translation bootstrap staging test missing marker: {marker}")

if FIXTURE_CORE_TEST.is_file():
    text = FIXTURE_CORE_TEST.read_text(encoding="utf-8")
    for marker in (
        "ValidPinnedManifestBuildsEncodeAndDecodeFixtures",
        "RevisionDriftFailsClosed",
        "HeaderCountsMustMatchParsedFixtures",
    ):
        if marker not in text:
            errors.append(f"tokenizer fixture parser test missing marker: {marker}")

if ONNX_TEST.is_file():
    text = ONNX_TEST.read_text(encoding="utf-8")
    for marker in (
        "ReferenceRuntimeUsesNonCachedThreeInputDecoder",
        "MeasuredShapesLockHiddenAndVocabularyDimensions",
        "ReferenceRuntimeSizeEqualsMeasuredEncoderPlusDecoder",
    ):
        if marker not in text:
            errors.append(f"OPUS-MT measured ONNX regression test missing marker: {marker}")

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
        "python tools/test_prepare_translation_tokenizer_fixture_manifest.py",
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
    "PASS: local translation assets are parity-gated, double-hash-verified, bootstrap support is revision/hash locked, "
    "the non-cached decoder baseline remains selected, and no remote fallback can mask a local runtime failure"
)
