#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROBE = ROOT / "tools" / "probe_translation_export.py"
SENTENCEPIECE_INSPECTOR = ROOT / "tools" / "inspect_translation_sentencepiece.py"
MANAGED_PREPARE = ROOT / "tools" / "prepare_managed_sentencepiece_manifest.py"
MANAGED_PROBE_PROJECT = ROOT / "tools" / "PhraseLayer.TokenizerParityProbe" / "PhraseLayer.TokenizerParityProbe.csproj"
MANAGED_PROBE_PROGRAM = ROOT / "tools" / "PhraseLayer.TokenizerParityProbe" / "Program.cs"
CORE_TOKENIZER = ROOT / "src" / "PhraseLayer.Core" / "ManagedSentencePieceTokenizer.cs"
CORE_MANIFEST = ROOT / "src" / "PhraseLayer.Core" / "ManagedSentencePieceManifest.cs"
CORE_CONTRACT = ROOT / "src" / "PhraseLayer.Core" / "OpusMtSentencePieceContract.cs"
CORE_TEST = ROOT / "tests" / "PhraseLayer.Core.Tests" / "ManagedSentencePieceTokenizerTests.cs"
CONTRACT_TEST = ROOT / "tests" / "PhraseLayer.Core.Tests" / "OpusMtSentencePieceContractTests.cs"
WORKFLOW = ROOT / ".github" / "workflows" / "translation-export-probe.yml"
REQUEST = ROOT / "ci" / "translation-export-probe.request"
DOC = ROOT / "docs" / "LOCAL_TRANSLATION.md"

errors: list[str] = []
for path in (
    PROBE,
    SENTENCEPIECE_INSPECTOR,
    MANAGED_PREPARE,
    MANAGED_PROBE_PROJECT,
    MANAGED_PROBE_PROGRAM,
    CORE_TOKENIZER,
    CORE_MANIFEST,
    CORE_CONTRACT,
    CORE_TEST,
    CONTRACT_TEST,
    WORKFLOW,
    REQUEST,
    DOC,
):
    if not path.is_file():
        errors.append(f"missing translation probe contract file: {path.relative_to(ROOT)}")

if PROBE.is_file():
    text = PROBE.read_text(encoding="utf-8")
    for marker in (
        'MODEL_ID = "Helsinki-NLP/opus-mt-en-jap"',
        'REVISION = "a863894cdd2b80f3bc1c5966734aee9ffec207d1"',
        'TASK = "text2text-generation-with-past"',
        '"num_beams": 4',
        "sha256_file(path)",
        "operator_counts",
        "external_data_locations",
        '"runtime_status": "unverified-real-unity-import-required"',
        "tokenizer_fixture(tokenizer)",
        '"input_ids": input_ids',
        '"attention_mask": attention_mask',
        '"decoded_skip_special_tokens"',
        '"tokens": [str(value) for value in tokenizer.convert_ids_to_tokens(input_ids)]',
        "compare_tokenizer_parity(tokenizer_reference, tokenizer_exported)",
        'report["tokenizer_parity"]["exact"]',
        "compare_parity(reference, onnx)",
        'report["parity"]["exact"]',
        '"encoder_plus_decoder"',
        "trust_remote_code=False",
        "do_validation=True",
        "monolith=False",
    ):
        if marker not in text:
            errors.append(f"translation parity probe missing reviewed marker: {marker}")

if SENTENCEPIECE_INSPECTOR.is_file():
    text = SENTENCEPIECE_INSPECTOR.read_text(encoding="utf-8")
    for marker in (
        'MODEL_FILES = ("source.spm", "target.spm")',
        "SentencePieceProcessor(model_file=str(path))",
        "processor.serialized_model_proto()",
        "pb.ModelProto()",
        '"model_type"',
        '"processor_ids"',
        '"byte_fallback"',
        '"split_by_whitespace"',
        '"normalizer"',
        '"precompiled_charsmap_sha256"',
        '"piece_type_counts"',
        '"runtime_compatibility": "unverified-managed-unity-tokenizer-required"',
        "verify_report_identity(report, item)",
    ):
        if marker not in text:
            errors.append(f"SentencePiece contract inspector missing reviewed marker: {marker}")

if MANAGED_PREPARE.is_file():
    text = MANAGED_PREPARE.read_text(encoding="utf-8")
    for marker in (
        'MAGIC = "PHRASELAYER_SENTENCEPIECE_UNIGRAM_V1"',
        "EXPECTED_SOURCE_NORMAL_PIECES = 15879",
        "EXPECTED_MARIAN_VOCAB = 46276",
        'vocab.get("</s>") != 0',
        'vocab.get("<unk>") != 1',
        'vocab.get("<pad>") != 46275',
        'source_model.pieces if piece.type == normal_type',
        '"status": "ephemeral-local-runtime-artifact"',
        '"uploaded": False',
        'report["managed_tokenizer_manifest"] = result',
    ):
        if marker not in text:
            errors.append(f"managed tokenizer manifest converter missing reviewed marker: {marker}")

if CORE_TOKENIZER.is_file():
    text = CORE_TOKENIZER.read_text(encoding="utf-8")
    for marker in (
        "sealed class ManagedSentencePieceUnigramTokenizer",
        "NormalizationForm.FormKC",
        "WhitespaceMarker = '\\u2581'",
        "bestScores",
        "unknownScore",
        "NextUnicodeScalarEnd",
        "reversed.Add(sourceEosTokenId)",
        "Target translation contains the unknown token",
    ):
        if marker not in text:
            errors.append(f"managed SentencePiece tokenizer missing reviewed marker: {marker}")

if CORE_MANIFEST.is_file():
    text = CORE_MANIFEST.read_text(encoding="utf-8")
    for marker in (
        'Magic = "PHRASELAYER_SENTENCEPIECE_UNIGRAM_V1"',
        'string.Equals(fields[0], "S", StringComparison.Ordinal)',
        'string.Equals(fields[0], "V", StringComparison.Ordinal)',
        "OpusMtEnJapSentencePieceContract.ValidateMeasuredManifest(measured)",
        "OpusMtEnJapMeasuredOnnxContract.VocabularySize",
        "new ManagedSentencePieceUnigramTokenizer(",
    ):
        if marker not in text:
            errors.append(f"managed SentencePiece manifest parser missing reviewed marker: {marker}")

if CORE_CONTRACT.is_file():
    text = CORE_CONTRACT.read_text(encoding="utf-8")
    for marker in (
        'ModelType = "UNIGRAM"',
        'NormalizerName = "nmt_nfkc"',
        'NormalizerCharsMapSha256 =',
        '"cab969cc39d743f8402e6fd752a0916e71839bcb27171ca72191336b7f71b4bc"',
        "SourcePieceCount = 15882",
        "TargetPieceCount = 32000",
        "ByteFallback = false",
        "ValidateMeasuredManifest",
    ):
        if marker not in text:
            errors.append(f"measured SentencePiece Core contract missing reviewed marker: {marker}")

if MANAGED_PROBE_PROJECT.is_file():
    text = MANAGED_PROBE_PROJECT.read_text(encoding="utf-8")
    for marker in (
        "<TargetFramework>net8.0</TargetFramework>",
        "<LangVersion>9.0</LangVersion>",
        "../../src/PhraseLayer.Core/PhraseLayer.Core.csproj",
    ):
        if marker not in text:
            errors.append(f"managed tokenizer parity project missing reviewed marker: {marker}")

if MANAGED_PROBE_PROGRAM.is_file():
    text = MANAGED_PROBE_PROGRAM.read_text(encoding="utf-8")
    for marker in (
        "ManagedSentencePieceManifest.ParseTokenizer",
        'RequiredArray(report, "tokenizer_reference", "samples")',
        'RequiredArray(report, "reference_samples")',
        "StripDecoderControlTokens",
        'report["managed_tokenizer_parity"]',
        '"source_encode"',
        '"target_decode"',
    ):
        if marker not in text:
            errors.append(f"managed tokenizer parity probe missing reviewed marker: {marker}")

for path, label, markers in (
    (
        CORE_TEST,
        "managed tokenizer regression test",
        (
            "NmtNfkcNormalizerAppliesCompatibilityAndMeasuredWhitespaceRules",
            "ViterbiChoosesHigherTotalUnigramScore",
            "UncoveredUnicodeScalarFallsBackToSingleUnknownToken",
            "DecodeFailsClosedOnUnknownTargetToken",
        ),
    ),
    (
        CONTRACT_TEST,
        "SentencePiece contract regression test",
        (
            "MeasuredManifestIsAccepted",
            "WrongModelTypeFailsClosed",
            "NormalizerCharsMapIdentityDriftFailsClosed",
            "PieceCountDriftFailsClosed",
        ),
    ),
):
    if path.is_file():
        text = path.read_text(encoding="utf-8")
        for marker in markers:
            if marker not in text:
                errors.append(f"{label} missing reviewed marker: {marker}")

if WORKFLOW.is_file():
    text = WORKFLOW.read_text(encoding="utf-8")
    for marker in (
        "workflow_dispatch:",
        "ci/translation-export-probe.request",
        "statuses: write",
        'dotnet-version: "8.0.423"',
        '"torch==2.13.0"',
        '"transformers==4.57.6"',
        '"optimum==2.1.0"',
        '"optimum-onnx[onnxruntime]==0.1.0"',
        '"onnx==1.22.0"',
        '"onnxruntime==1.29.0"',
        '"sentencepiece==0.2.2"',
        "python tools/probe_translation_export.py",
        "python tools/inspect_translation_sentencepiece.py",
        "python tools/prepare_managed_sentencepiece_manifest.py",
        "tools/PhraseLayer.TokenizerParityProbe/PhraseLayer.TokenizerParityProbe.csproj",
        "MANAGED_MANIFEST_EXIT_CODE",
        "MANAGED_PARITY_EXIT_CODE",
        "managed_tokenizer_parity",
        "translation-export-probe.json",
        "phraselayer/translation-export-probe",
    ):
        if marker not in text:
            errors.append(f"translation workflow missing reviewed marker: {marker}")
    if "phraselayer-sentencepiece-unigram-v1.txt" in text and "path: |\n            .ci/translation-export-probe.json" not in text:
        errors.append("managed SentencePiece runtime manifest must not be uploaded as probe artifact")

if REQUEST.is_file():
    text = REQUEST.read_text(encoding="utf-8")
    for marker in (
        "revision=a863894cdd2b80f3bc1c5966734aee9ffec207d1",
        "policy=metadata-only-no-weight-artifact",
        "require_token_exact_reference_parity=true",
        "require_tokenizer_encode_decode_parity=true",
    ):
        if marker not in text:
            errors.append(f"translation probe request missing reviewed marker: {marker}")

if DOC.is_file():
    text = DOC.read_text(encoding="utf-8")
    folded = text.casefold()
    for marker in (
        "revision-pinned source",
        "token-exact parity",
        "hash-pinned",
        "real unity import",
        "quest",
        "bundled=false",
        "metadata-only",
        "translation quality gate",
    ):
        if marker.casefold() not in folded:
            errors.append(f"local translation doc missing gate marker: {marker}")

if errors:
    raise SystemExit("\n".join(errors))

print(
    "PASS: OPUS-MT export probe locks measured SentencePiece internals, deterministic managed-manifest conversion, "
    "real C# tokenizer source/target parity, metadata-only artifacts, quality separation, and real-Unity/Quest gates"
)
