#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "tools" / "run_marian_android_runtime_smoke.py"
spec = importlib.util.spec_from_file_location("run_marian_android_runtime_smoke", MODULE_PATH)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)


def expect_raises(fn, exc_type) -> None:
    try:
        fn()
    except exc_type:
        return
    raise AssertionError(f"expected {exc_type.__name__}")


def main() -> None:
    devices = module.parse_adb_devices(
        "List of devices attached\n"
        "ABC123 device product:test model:Android_Device device:test transport_id:1\n"
        "emulator-5554 offline transport_id:2\n"
    )
    assert devices == ["ABC123"]
    assert module.choose_serial(devices, None) == "ABC123"
    assert module.choose_serial(devices, "ABC123") == "ABC123"
    expect_raises(lambda: module.choose_serial([], None), module.SmokeError)
    expect_raises(lambda: module.choose_serial(["a", "b"], None), module.SmokeError)
    expect_raises(lambda: module.choose_serial(["a"], "b"), module.SmokeError)

    assert module.parse_abis("arm64-v8a,armeabi-v7a") == ["arm64-v8a", "armeabi-v7a"]
    assert module.require_arm64_abi("arm64-v8a,armeabi-v7a", "") == ["arm64-v8a", "armeabi-v7a"]
    assert module.require_arm64_abi("", "arm64-v8a") == ["arm64-v8a"]
    expect_raises(lambda: module.require_arm64_abi("x86_64", "x86_64"), module.SmokeError)

    passed = module.readiness_from_logcat(
        "PhraseLayer Marian Android runtime smoke PASS\n"
        "elapsed_ms=1234.5 bootstrap_ready=true translation_override=true assisted_units=1 segments=1 reference_match=true display_length=4\n"
        "translation_runtime=MarianOpusMtEnJa generation_backend=UnityMarianDeviceResidentGenerationBackend tokenizer_runtime=Microsoft.ML.Tokenizers semantic_span_pipeline=true product_translation_gate=true\n"
    )
    assert passed["runtime_smoke_passed"] is True
    assert passed["exact_reference_match_observed"] is True
    assert passed["product_translation_gate_observed"] is True
    assert passed["device_resident_backend_observed"] is True
    assert passed["successful_pipeline_state_observed"] is True
    assert passed["fatal_exception"] is False

    misleading = module.readiness_from_logcat(
        "PhraseLayer Marian Android runtime smoke PASS\n"
        "elapsed_ms=1234.5 bootstrap_ready=false translation_override=false assisted_units=0 segments=0 reference_match=true display_length=0\n"
        "translation_runtime=MarianOpusMtEnJa generation_backend=UnityMarianDeviceResidentGenerationBackend tokenizer_runtime=Microsoft.ML.Tokenizers semantic_span_pipeline=true product_translation_gate=true\n"
    )
    assert misleading["runtime_smoke_passed"] is True
    assert misleading["exact_reference_match_observed"] is True
    assert misleading["successful_pipeline_state_observed"] is False

    failed = module.readiness_from_logcat(
        "PhraseLayer Marian Android runtime smoke FAIL_EXCEPTION\nFATAL EXCEPTION\n"
    )
    assert failed["runtime_smoke_failed"] is True
    assert failed["fatal_exception"] is True

    raw_log = (
        "09-02 12:00:00.000 100 100 I Unity : PhraseLayer Marian Android runtime smoke PASS\n"
        "09-02 12:00:00.001 100 100 I Unity : elapsed_ms=1234.5 bootstrap_ready=true translation_override=true assisted_units=1 segments=1 reference_match=true display_length=4\n"
        "09-02 12:00:00.002 100 100 I Unity : translation_runtime=MarianOpusMtEnJa generation_backend=UnityMarianDeviceResidentGenerationBackend tokenizer_runtime=Microsoft.ML.Tokenizers semantic_span_pipeline=true product_translation_gate=true\n"
        "09-02 12:00:00.003 100 100 I Unity : fixture_source=keep-off translated_text=<redacted; exact offline reference match required>\n"
        "09-02 12:00:00.004 100 100 I Unity : translated_text=PRIVATE OUTPUT\n"
        "09-02 12:00:00.005 100 100 I Unity : elapsed_ms=55.0 bootstrap_ready=false translation_override=false assisted_units=0 segments=0 reference_match=false display_length=0\n"
        "09-02 12:00:00.006 100 100 I Unity : elapsed_ms=1234.5 bootstrap_ready=true translation_override=true assisted_units=1 segments=1 reference_match=true display_length=4 secret=LEAK\n"
        "09-02 12:00:00.007 100 100 E AndroidRuntime : FATAL EXCEPTION: main private-stack\n"
    )
    sanitized = module.sanitize_logcat_diagnostics(raw_log)
    lines = sanitized.splitlines()
    assert "PhraseLayer Marian Android runtime smoke PASS" in lines
    assert "elapsed_ms=1234.5 bootstrap_ready=true translation_override=true assisted_units=1 segments=1 reference_match=true display_length=4" in lines
    assert "elapsed_ms=55.0 bootstrap_ready=false translation_override=false assisted_units=0 segments=0 reference_match=false display_length=0" in lines
    assert "translation_runtime=MarianOpusMtEnJa generation_backend=UnityMarianDeviceResidentGenerationBackend tokenizer_runtime=Microsoft.ML.Tokenizers semantic_span_pipeline=true product_translation_gate=true" in lines
    assert "fixture_source=keep-off translated_text=<redacted; exact offline reference match required>" in lines
    assert "FATAL EXCEPTION" in lines
    assert len(lines) == 6
    assert "PRIVATE OUTPUT" not in sanitized
    assert "LEAK" not in sanitized
    assert "private-stack" not in sanitized
    assert "09-02 12:00:00" not in sanitized

    redacted = module.redact_failure_message(
        module.SmokeError("adb -s ABC123 install failed"),
        "ABC123",
    )
    assert "ABC123" not in redacted
    assert "<redacted-adb-serial>" in redacted
    fingerprint = module.serial_fingerprint("ABC123")
    assert fingerprint is not None and len(fingerprint) == 12

    source = MODULE_PATH.read_text(encoding="utf-8")
    assert 'DEFAULT_PACKAGE = "com.unjuno.phraselayer.marianfixture"' in source
    assert 'PASS_MARKER = "PhraseLayer Marian Android runtime smoke PASS"' in source
    assert 'SUCCESS_STATE_MARKER = "bootstrap_ready=true translation_override=true assisted_units=1 segments=1 reference_match=true"' in source
    assert '"android_runtime_execution_performed": runtime_started' in source
    assert '"quest_device_execution_performed": False' in source
    assert '"network_required": False' in source
    assert '"raw_process_logcat_written_to_disk": False' in source
    assert '"raw_process_logcat_uploaded": False' in source
    assert '"raw_command_stderr_in_failure_evidence": False' in source
    assert 'pattern.fullmatch(candidate)' in source
    assert 'diagnostics_path.write_text(sanitize_logcat_diagnostics(logcat)' in source
    assert 'completed.stderr.strip()' not in source
    assert 'logcat.txt' not in source
    assert '"adb_serial": serial' not in source

    print(
        "PASS: Marian Android runtime smoke device selection, ARM64 requirement, coherent PASS state, truthful execution evidence, "
        "full-grammar diagnostics, translated-text privacy, serial redaction and product-gate evidence contracts"
    )


if __name__ == "__main__":
    main()
