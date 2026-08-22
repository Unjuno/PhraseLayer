#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PREPARE = ROOT / "tools" / "prepare_unity_translation_assets.py"
TEST = ROOT / "tools" / "test_prepare_unity_translation_assets.py"
GITIGNORE = ROOT / ".gitignore"
WORKFLOW = ROOT / ".github" / "workflows" / "core-ci.yml"
DOC = ROOT / "docs" / "LOCAL_TRANSLATION.md"

errors: list[str] = []
for path in (PREPARE, TEST, GITIGNORE, WORKFLOW, DOC):
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

print("PASS: local translation assets are parity-gated, hash-verified, git-ignored, and not promoted as Unity-compatible")
