#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROBE = ROOT / "tools" / "probe_translation_export.py"
WORKFLOW = ROOT / ".github" / "workflows" / "translation-export-probe.yml"
REQUEST = ROOT / "ci" / "translation-export-probe.request"
DOC = ROOT / "docs" / "LOCAL_TRANSLATION.md"

errors: list[str] = []
for path in (PROBE, WORKFLOW, REQUEST, DOC):
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
        "compare_parity(reference, onnx)",
        'report["parity"]["exact"]',
        "trust_remote_code=False",
        "do_validation=True",
        "monolith=False",
    ):
        if marker not in text:
            errors.append(f"translation parity probe missing reviewed marker: {marker}")

if WORKFLOW.is_file():
    text = WORKFLOW.read_text(encoding="utf-8")
    for marker in (
        "workflow_dispatch:",
        "ci/translation-export-probe.request",
        "statuses: write",
        '"torch==2.13.0"',
        '"transformers==4.57.6"',
        '"optimum==2.1.0"',
        '"optimum-onnx[onnxruntime]==0.1.0"',
        '"onnx==1.22.0"',
        '"onnxruntime==1.29.0"',
        '"sentencepiece==0.2.2"',
        "python tools/probe_translation_export.py",
        "translation-export-probe.json",
        "phraselayer/translation-export-probe",
    ):
        if marker not in text:
            errors.append(f"translation workflow missing reviewed marker: {marker}")
    if "artifacts/translation-export/opus-mt-en-jap/**" in text:
        errors.append("translation workflow must not upload model-weight artifacts")

if REQUEST.is_file():
    text = REQUEST.read_text(encoding="utf-8")
    for marker in (
        "revision=a863894cdd2b80f3bc1c5966734aee9ffec207d1",
        "policy=metadata-only-no-weight-artifact",
        "require_token_exact_reference_parity=true",
    ):
        if marker not in text:
            errors.append(f"translation probe request missing reviewed marker: {marker}")

if DOC.is_file():
    text = DOC.read_text(encoding="utf-8")
    for marker in (
        "revision-pinned source",
        "token-exact parity",
        "hash-pinned",
        "real Unity import",
        "Quest",
        "bundled=false",
        "metadata-only",
    ):
        if marker not in text:
            errors.append(f"local translation doc missing gate marker: {marker}")

if errors:
    raise SystemExit("\n".join(errors))

print("PASS: OPUS-MT export/parity probe preserves metadata-only artifacts and real-Unity/Quest promotion gates")
