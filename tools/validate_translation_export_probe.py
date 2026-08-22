#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EXPORTER = ROOT / "tools" / "export_opus_mt_onnx.py"
TEST = ROOT / "tools" / "test_export_opus_mt_onnx.py"
WORKFLOW = ROOT / ".github" / "workflows" / "translation-export-probe.yml"
DOC = ROOT / "docs" / "LOCAL_TRANSLATION.md"

errors: list[str] = []
for path in (EXPORTER, TEST, WORKFLOW, DOC):
    if not path.is_file():
        errors.append(f"missing translation export probe file: {path.relative_to(ROOT)}")

if EXPORTER.is_file():
    text = EXPORTER.read_text(encoding="utf-8")
    for marker in (
        '"a863894cdd2b80f3bc1c5966734aee9ffec207d1"',
        'EXPORT_TASK = "text2text-generation"',
        "from optimum.exporters.onnx import main_export",
        'revision=candidate["revision"]',
        "trust_remote_code=False",
        "do_validation=True",
        "monolith=False",
        'output_dir.rglob("*")',
        '"sha256": sha256_file(path)',
        '"unverified-real-unity-import-required"',
    ):
        if marker not in text:
            errors.append(f"translation exporter missing reviewed marker: {marker}")

if WORKFLOW.is_file():
    text = WORKFLOW.read_text(encoding="utf-8")
    if "workflow_dispatch:" not in text:
        errors.append("translation export probe must be manual workflow_dispatch only")
    for forbidden in ("\n  push:", "\n  pull_request:", "schedule:"):
        if forbidden in text:
            errors.append(f"translation export probe must not auto-run via {forbidden.strip()}")
    for marker in (
        "optimum[onnx]==2.3.0",
        "sentencepiece==0.2.2",
        "python tools/export_opus_mt_onnx.py",
        "translation-export.manifest.json",
        "actions/upload-artifact@v4",
    ):
        if marker not in text:
            errors.append(f"translation export workflow missing reviewed marker: {marker}")

if DOC.is_file():
    text = DOC.read_text(encoding="utf-8")
    for marker in (
        "revision-pinned source",
        "ONNX export",
        "hash-pinned",
        "real Unity import",
        "Quest",
        "bundled=false",
    ):
        if marker not in text:
            errors.append(f"local translation doc missing gate marker: {marker}")

if errors:
    raise SystemExit("\n".join(errors))

print("PASS: manual OPUS-MT export probe is revision-pinned, content-addressed, and gated before Unity/Quest claims")
