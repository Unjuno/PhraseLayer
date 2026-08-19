#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "PhraseLayer.Core"
UNITY = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts"

checks = {
    CORE / "PaddleOcrDictionaryManifest.cs": (
        "ExpectedModelId = \"pp-ocrv6-tiny-rec\"",
        "ExpectedRevision = \"2612ab37152ae0a677521bae4e1e3d4fb4cf7c30\"",
        "raw_token_count does not match the assigned dictionary",
        "use_space_char does not match the Unity bootstrap setting",
        "Dictionary SHA-256 does not match the generated manifest",
    ),
    UNITY / "UnityPaddleOcrDictionaryManifest.cs": (
        "JsonUtility.FromJson<ManifestJson>",
        "PaddleOcrCharacterDictionary.Parse(",
        "dictionaryAsset.bytes",
        "SHA256.Create()",
        "PaddleOcrDictionaryManifestContract.ValidateAndBuildReport",
    ),
    UNITY / "UnityPaddleOcrBootstrapBehaviour.cs": (
        "characterDictionaryManifest",
        "UnityPaddleOcrDictionaryManifest.Validate(",
        "DictionaryManifestReport",
        "Assign the generated PP-OCR dictionary manifest TextAsset",
    ),
}

violations = []
for path, markers in checks.items():
    if not path.is_file():
        violations.append(f"missing file: {path.relative_to(ROOT)}")
        continue
    text = path.read_text(encoding="utf-8")
    for marker in markers:
        if marker not in text:
            violations.append(f"{path.relative_to(ROOT)} missing marker: {marker}")

if violations:
    raise SystemExit("\n".join(violations))

print("PASS: PP-OCR dictionary manifest identity/token/space/hash validation is wired into Unity bootstrap")
