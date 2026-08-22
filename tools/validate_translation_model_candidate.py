#!/usr/bin/env python3
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LOCK = ROOT / "models" / "models.lock.json"

manifest = json.loads(LOCK.read_text(encoding="utf-8"))
candidates = manifest.get("candidates", [])
translation = next((item for item in candidates if item.get("purpose") == "translation-en-ja"), None)
if translation is None:
    raise SystemExit("models.lock.json is missing the translation-en-ja candidate")

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
    "export_format": "onnx-required-not-yet-produced",
    "export_status": "not-produced",
    "export_filenames": "unverified-until-export",
    "runtime_target": "com.unity.ai.inference@2.2.1",
    "runtime_compatibility": "unverified-reviewed-onnx-export-required",
    "license": "Apache-2.0",
    "license_status": "full-upstream-revision-and-license-metadata-verified; export-redistribution-review-pending",
    "bundled": False,
}

violations = []
for key, value in expected.items():
    if translation.get(key) != value:
        violations.append(f"translation candidate {key} expected {value!r} but found {translation.get(key)!r}")

revision = translation.get("revision", "")
if re.fullmatch(r"[0-9a-f]{40}", revision) is None:
    violations.append("translation revision must be a full 40-character Git revision")

expected_source_artifacts = [
    "config.json",
    "generation_config.json",
    "pytorch_model.bin",
    "source.spm",
    "target.spm",
    "tokenizer_config.json",
    "vocab.json",
]
if translation.get("upstream_source_artifacts") != expected_source_artifacts:
    violations.append("translation upstream_source_artifacts drift from the reviewed revision tree")

if translation.get("required_export_components") != ["encoder", "decoder"]:
    violations.append("translation ONNX export must retain explicit encoder and decoder components")

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
if translation.get("generation_contract") != expected_generation:
    violations.append("translation generation_contract drift from the pinned upstream generation config")

# Do not let a guessed ONNX filename become a false reproducibility claim. The export must be produced,
# inspected, hash-pinned, and then promoted from this component-level contract.
if "required_export_artifacts" in translation:
    violations.append("translation candidate must not claim unproduced ONNX filenames as required_export_artifacts")

if translation.get("bundled") is True:
    violations.append("translation model must remain unbundled until exported artifacts are hash-pinned and Quest-tested")

if violations:
    raise SystemExit("\n".join(violations))

print("PASS: OPUS-MT source revision, source artifacts, generation contract, and unproduced ONNX boundary are pinned")
