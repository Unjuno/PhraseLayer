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
    "architecture": "marian",
    "source_language": "en",
    "target_language": "ja",
    "tokenization": "SentencePiece",
    "export_format": "onnx-required-not-yet-produced",
    "runtime_target": "com.unity.ai.inference@2.2.1",
    "runtime_compatibility": "unverified-reviewed-onnx-export-required",
    "bundled": False,
}

violations = []
for key, value in expected.items():
    if translation.get(key) != value:
        violations.append(f"translation candidate {key} expected {value!r} but found {translation.get(key)!r}")

observed = translation.get("upstream_head_observed", "")
if re.fullmatch(r"[0-9a-f]{7,40}", observed) is None:
    violations.append("translation upstream_head_observed must be a hexadecimal Git revision observation")

required_artifacts = translation.get("required_export_artifacts")
expected_artifacts = [
    "encoder_model.onnx",
    "decoder_model.onnx",
    "source.spm",
    "target.spm",
    "vocab.json",
    "generation_config.json",
]
if required_artifacts != expected_artifacts:
    violations.append(
        "translation required_export_artifacts must remain the reviewed encoder/decoder/tokenizer/generation baseline"
    )

# A short observed upstream HEAD is not sufficient for redistribution. A real export may only become bundleable
# after the lock file is upgraded to a full revision plus hash-pinned exported artifacts.
if translation.get("bundled") is True and len(observed) != 40:
    violations.append("translation model cannot be bundled without a full 40-character upstream revision")

if violations:
    raise SystemExit("\n".join(violations))

print("PASS: local translation candidate remains unbundled and export-gated with Marian/SentencePiece requirements")
