#!/usr/bin/env python3
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LOCK = ROOT / "models" / "models.lock.json"
CORE_CONTRACT = ROOT / "src" / "PhraseLayer.Core" / "OpusMtTranslationContract.cs"
CORE_ONNX = ROOT / "src" / "PhraseLayer.Core" / "OpusMtOnnxExportMetadata.cs"

manifest = json.loads(LOCK.read_text(encoding="utf-8"))
candidates = manifest.get("candidates", [])
translation = next((item for item in candidates if item.get("purpose") == "translation-en-ja"), None)
if translation is None:
    raise SystemExit("models.lock.json is missing the translation-en-ja candidate")

violations = []


def require(condition, message):
    if not condition:
        violations.append(message)


expected = {
    "id": "opus-mt-en-jap",
    "upstream": "Helsinki-NLP/opus-mt-en-jap",
    "revision": "a863894cdd2b80f3bc1c5966734aee9ffec207d1",
    "architecture": "marian",
    "source_language": "en",
    "target_language": "ja",
    "upstream_target_language": "jap",
    "tokenization": "SentencePiece",
    "upstream_weight_artifact": "pytorch_model.bin",
    "export_format": "onnx",
    "export_status": "measured-token-exact-parity",
    "runtime_target": "com.unity.ai.inference@2.2.1",
    "runtime_compatibility": "unverified-real-unity-import-required",
    "quality_status": "candidate-quality-review-required",
    "license": "Apache-2.0",
    "license_status": "full-upstream-revision-and-license-metadata-verified; exported-artifact-redistribution-review-pending",
    "bundled": False,
}
for key, value in expected.items():
    require(
        translation.get(key) == value,
        f"translation candidate {key} expected {value!r} but found {translation.get(key)!r}",
    )

require(
    re.fullmatch(r"[0-9a-f]{40}", str(translation.get("revision", ""))) is not None,
    "translation revision must be a full 40-character Git revision",
)

expected_source_artifacts = [
    "config.json",
    "generation_config.json",
    "pytorch_model.bin",
    "source.spm",
    "target.spm",
    "tokenizer_config.json",
    "vocab.json",
]
require(
    translation.get("upstream_source_artifacts") == expected_source_artifacts,
    "translation upstream_source_artifacts drift from the reviewed revision tree",
)
require(
    translation.get("required_export_components") == ["encoder", "decoder"],
    "translation ONNX export must retain explicit encoder and decoder components",
)
require(
    translation.get("export_filenames") == [
        "encoder_model.onnx",
        "decoder_model.onnx",
        "decoder_model_merged.onnx",
        "decoder_with_past_model.onnx",
    ],
    "translation export_filenames drift from the measured Optimum export",
)

expected_generation = {
    "bos_token_id": 0,
    "decoder_start_token_id": 46275,
    "eos_token_id": 0,
    "forced_eos_token_id": 0,
    "pad_token_id": 46275,
    "max_length": 512,
    "num_beams": 4,
    "renormalize_logits": True,
}
require(
    translation.get("generation_contract") == expected_generation,
    "translation generation_contract drift from the pinned upstream generation config",
)

probe = translation.get("export_probe")
require(isinstance(probe, dict), "translation export_probe must be a measured object")
if isinstance(probe, dict):
    require(probe.get("schema_version") == 4, "translation export probe schema must remain 4")
    require(probe.get("task") == "text2text-generation-with-past", "translation export task drift")
    require(
        probe.get("first_measured_commit") == "792055c78981de4dfaf2a4b38865793005a546cb",
        "translation first measured export commit drift",
    )
    require(
        probe.get("repeat_verified_commit") == "8be5cc2ec258aec314cc9deb5a76485415e608b0",
        "translation repeat verification commit drift",
    )
    require(probe.get("tokenizer_parity_exact") is True, "translation tokenizer parity must be exact")
    require(probe.get("generation_parity_exact") is True, "translation generation parity must be exact")
    require(probe.get("sample_count") == 2, "translation measured parity sample count drift")
    toolchain = probe.get("toolchain", {})
    expected_toolchain = {
        "python": "3.11.16",
        "torch": "2.13.0",
        "transformers": "4.57.6",
        "optimum": "2.1.0",
        "optimum_onnx": "0.1.0",
        "onnx": "1.22.0",
        "onnxruntime": "1.29.0",
        "sentencepiece": "0.2.2",
    }
    require(toolchain == expected_toolchain, "translation measured export toolchain drift")

expected_exports = [
    {
        "role": "encoder-reference",
        "artifact": "encoder_model.onnx",
        "artifact_size_bytes": 171553398,
        "artifact_sha256": "bb0d8d22053062bbd3695a468c88d1f84367eb195fa5f9fb75aa6c9548f57c59",
        "ir_version": 8,
        "opset": 18,
    },
    {
        "role": "decoder-reference",
        "artifact": "decoder_model.onnx",
        "artifact_size_bytes": 291878261,
        "artifact_sha256": "513bbf05f48da69847ce247e3245a5e84a814a7e591e8f544dea4854d202dc00",
        "ir_version": 8,
        "opset": 18,
    },
    {
        "role": "decoder-merged-experimental",
        "artifact": "decoder_model_merged.onnx",
        "artifact_size_bytes": 292059873,
        "artifact_sha256": "88014b5ab5e7c32062a6d2146eb3cd96ce2ee060ea2c3df5f936b2505e52f141",
        "ir_version": 8,
        "opset": 18,
    },
    {
        "role": "decoder-with-past-experimental",
        "artifact": "decoder_with_past_model.onnx",
        "artifact_size_bytes": 279248654,
        "artifact_sha256": "0e1692385e64eaedd256d31a6ee5dd3ee630c79126bd97d6d982ba6eca919ae3",
        "ir_version": 8,
        "opset": 18,
    },
]
require(
    translation.get("export_artifacts") == expected_exports,
    "translation export artifact hashes/sizes drift from the repeat-verified probe",
)

expected_support = {
    "config.json": (1298, "19571fb6bcab20ef65689b694e3c0284a5f7d0e4c93fcfa59467aedd66c4bfef"),
    "generation_config.json": (288, "1e8464ebcd1ca238b32d9701bd045f51e90a46cd0a3adb5bb7dafa21251d50fc"),
    "source.spm": (508602, "375cbed8885a6d369e0493acfc69a066010a86f98f9bac02430cbeb1726934a6"),
    "special_tokens_map.json": (74, "5e4d1f5e759d74cb1c2fe1d165cfc62b5237aa904de759380cd6f43042eec723"),
    "target.spm": (1021944, "7d5ec21daca7dccb7a9df371b699def40ddd9d0c24cef855e44e31a39b96af55"),
    "tokenizer_config.json": (848, "a6688b81aff2f95033ea8da0552e4198d737dfaedee9fc645fbcd0d9d81f81e5"),
    "vocab.json": (1734978, "62f7857585e3cd6150bb420830076edede27caac6304778d8d81be41164e469d"),
}
support = translation.get("export_support_artifacts")
require(isinstance(support, list), "translation export_support_artifacts must be a list")
if isinstance(support, list):
    measured_support = {
        item.get("artifact"): (item.get("artifact_size_bytes"), item.get("artifact_sha256"))
        for item in support
        if isinstance(item, dict)
    }
    require(measured_support == expected_support, "translation support artifact hashes/sizes drift")

require(
    translation.get("reference_runtime") == {
        "artifacts": ["encoder_model.onnx", "decoder_model.onnx"],
        "total_size_bytes": 463431659,
        "total_size_mib": 441.963,
    },
    "translation reference runtime set must remain the measured non-cached encoder+decoder pair",
)

quality_note = str(translation.get("quality_note", ""))
require(
    "parity" in quality_note.casefold() and "quality" in quality_note.casefold(),
    "translation lock must state that export parity is not product-quality evidence",
)

# The platform-neutral search policy must consume exactly the same pinned generation values.
if not CORE_CONTRACT.is_file():
    violations.append("missing Core OPUS-MT generation contract: src/PhraseLayer.Core/OpusMtTranslationContract.cs")
else:
    core = CORE_CONTRACT.read_text(encoding="utf-8")
    expected_core_constants = {
        "BosTokenId": expected_generation["bos_token_id"],
        "DecoderStartTokenId": expected_generation["decoder_start_token_id"],
        "EosTokenId": expected_generation["eos_token_id"],
        "ForcedEosTokenId": expected_generation["forced_eos_token_id"],
        "PadTokenId": expected_generation["pad_token_id"],
        "MaxLength": expected_generation["max_length"],
        "BeamWidth": expected_generation["num_beams"],
    }
    for name, value in expected_core_constants.items():
        marker = f"public const int {name} = {value};"
        require(marker in core, f"Core OPUS-MT generation contract missing pinned marker: {marker}")
    for marker in (
        "public const double LengthPenalty = 1.0;",
        "new ForcedEosTranslationBackend(",
        "ForcedEosTokenId,",
        "MaxLength);",
        "new TranslationGenerationOptions(",
        "beamWidth: BeamWidth",
        "maxLength: MaxLength",
    ):
        require(marker in core, f"Core OPUS-MT generation contract missing reviewed behavior: {marker}")

if not CORE_ONNX.is_file():
    violations.append("missing measured Core OPUS-MT ONNX contract")
else:
    onnx = CORE_ONNX.read_text(encoding="utf-8")
    for marker in (
        "ReferenceRuntimeSizeBytes = 463431659",
        '"bb0d8d22053062bbd3695a468c88d1f84367eb195fa5f9fb75aa6c9548f57c59"',
        '"513bbf05f48da69847ce247e3245a5e84a814a7e591e8f544dea4854d202dc00"',
        "HiddenSize = 512",
        "VocabularySize = 46276",
    ):
        require(marker in onnx, f"Core measured OPUS-MT ONNX contract missing lock marker: {marker}")

require(
    translation.get("bundled") is False,
    "translation model must remain unbundled until real Unity/Quest and distribution review gates pass",
)

if violations:
    raise SystemExit("\n".join(violations))

print(
    "PASS: OPUS-MT source revision, repeat-verified token-exact ONNX export hashes, generation contract, "
    "quality hold, and unbundled real-Unity/Quest gates are pinned"
)
