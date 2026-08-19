#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts"

violations = []


def require_file(path: Path) -> str:
    if not path.is_file():
        violations.append(f"missing file: {path.relative_to(ROOT)}")
        return ""
    return path.read_text(encoding="utf-8")


driver = require_file(SCRIPTS / "OcrDebugRuntimeBehaviour.cs")
for marker in (
    "public bool AutoRun",
    "autoRun = value",
    "new OcrFrameScheduler(engine, targetOcrHz)",
):
    if marker not in driver:
        violations.append(f"OCR runtime driver missing smoke-test control marker: {marker}")

presenter = require_file(SCRIPTS / "OcrViewportDebugBehaviour.cs")
for marker in (
    "loadSyntheticFixtureOnStart",
    "public bool LoadSyntheticFixtureOnStart",
    "if (loadSyntheticFixtureOnStart)",
    "LoadSyntheticFixture();",
    "public void Clear()",
):
    if marker not in presenter:
        violations.append(f"OCR presenter missing synthetic-fixture isolation marker: {marker}")

smoke = require_file(SCRIPTS / "QuestOcrSmokeTestBehaviour.cs")
for marker in (
    "timeoutSeconds = 60f",
    "retryIntervalSeconds = 0.25f",
    "minimumRecognizedRegions = 1",
    "includeRecognizedTextInReport",
    "presenter.LoadSyntheticFixtureOnStart = false",
    "presenter.Clear()",
    "var previousAutoRun = runtimeDriver.AutoRun",
    "runtimeDriver.AutoRun = false",
    "runtimeDriver.RunOnceAsync(cancellationToken)",
    "lastResult.CameraState == CameraCaptureState.Failed",
    "presenter.Regions.Count >= minimumRecognizedRegions",
    '"recognizer=unobserved"',
    '"FAIL_TIMEOUT"',
    '"FAIL_CAMERA"',
    '"FAIL_EXCEPTION"',
    '"PASS"',
    '"regions="',
    '"overall_confidence="',
    '"text_length="',
    '"dictionary_manifest=" + bootstrap.DictionaryManifestReport',
    '"runtime_contract=" + bootstrap.RuntimeContractReport',
    'builder.AppendLine("recognized_text=" + presenter.LastText);',
    '"recognized_text=<redacted; enable includeRecognizedTextInReport explicitly>"',
    "if (includeRecognizedTextInReport)",
    "runtimeDriver.AutoRun = previousAutoRun",
):
    if marker not in smoke:
        violations.append(f"Quest OCR smoke harness missing reviewed marker: {marker}")

if violations:
    raise SystemExit("\n".join(violations))

print("PASS: Quest OCR smoke harness isolates synthetic fixtures, serializes pump ownership, requires real OCR regions, and redacts recognized text by default")
