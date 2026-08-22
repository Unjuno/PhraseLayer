#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import json
import tempfile
from pathlib import Path

import prepare_unity_translation_assets as prepare


def identity(path: Path) -> dict:
    data = path.read_bytes()
    return {
        "path": path.name,
        "size_bytes": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
        "onnx": {
            "inputs": [{"name": "input_ids", "shape": ["batch", "sequence"]}],
            "outputs": [{"name": "output", "shape": ["batch", "sequence", 512]}],
        },
    }


def support_identity(path: Path) -> dict:
    data = path.read_bytes()
    return {
        "path": path.name,
        "size_bytes": len(data),
        "sha256": hashlib.sha256(data).hexdigest(),
    }


def write_fixture(root: Path) -> tuple[Path, Path, Path]:
    export_root = root / "export"
    export_root.mkdir()
    encoder = export_root / "encoder_model.onnx"
    decoder = export_root / "decoder_model_merged.onnx"
    tokenizer = export_root / "source.spm"
    encoder.write_bytes(b"encoder-bytes")
    decoder.write_bytes(b"decoder-bytes")
    tokenizer.write_bytes(b"sentencepiece-bytes")

    report = {
        "schema_version": 3,
        "model_id": prepare.EXPECTED_MODEL_ID,
        "revision": prepare.EXPECTED_REVISION,
        "status": "pass",
        "runtime_status": prepare.EXPECTED_RUNTIME_STATUS,
        "parity": {"exact": True},
        "files": [identity(encoder), identity(decoder), support_identity(tokenizer)],
    }
    report_path = root / "translation-export-probe.json"
    report_path.write_text(json.dumps(report), encoding="utf-8")

    unity = root / "unity"
    (unity / "Assets").mkdir(parents=True)
    return report_path, export_root, unity


def test_prepare_copies_only_verified_report_artifacts() -> None:
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        report_path, export_root, unity = write_fixture(root)
        destination = unity / prepare.DEFAULT_LOCAL_ASSET_RELATIVE
        destination.mkdir(parents=True)
        (destination / "stale.bin").write_bytes(b"stale")

        result = prepare.prepare(report_path, export_root, unity)

        assert result["file_count"] == 3
        assert result["onnx_count"] == 2
        assert not (destination / "stale.bin").exists()
        assert (destination / "encoder_model.onnx").read_bytes() == b"encoder-bytes"
        assert (destination / "decoder_model_merged.onnx").read_bytes() == b"decoder-bytes"
        manifest = json.loads((destination / prepare.LOCAL_MANIFEST_NAME).read_text(encoding="utf-8"))
        assert manifest["reference_parity_exact"] is True
        assert manifest["runtime_status"] == prepare.EXPECTED_RUNTIME_STATUS
        assert manifest["model_id"] == prepare.EXPECTED_MODEL_ID
        assert len(manifest["files"]) == 3


def test_prepare_rejects_hash_mismatch_without_replacing_previous_assets() -> None:
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        report_path, export_root, unity = write_fixture(root)
        destination = unity / prepare.DEFAULT_LOCAL_ASSET_RELATIVE
        destination.mkdir(parents=True)
        (destination / "keep.txt").write_text("previous", encoding="utf-8")
        (export_root / "encoder_model.onnx").write_bytes(b"tampered")

        try:
            prepare.prepare(report_path, export_root, unity)
        except prepare.PrepareTranslationError as error:
            assert "identity mismatch" in str(error)
        else:
            raise AssertionError("tampered export must be rejected")

        assert (destination / "keep.txt").read_text(encoding="utf-8") == "previous"


def test_prepare_rejects_non_exact_parity() -> None:
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        report_path, export_root, unity = write_fixture(root)
        report = json.loads(report_path.read_text(encoding="utf-8"))
        report["parity"]["exact"] = False
        report_path.write_text(json.dumps(report), encoding="utf-8")

        try:
            prepare.prepare(report_path, export_root, unity)
        except prepare.PrepareTranslationError as error:
            assert "token-exact" in str(error)
        else:
            raise AssertionError("non-exact parity report must be rejected")


def test_report_rejects_path_traversal() -> None:
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        report_path, _, _ = write_fixture(root)
        report = json.loads(report_path.read_text(encoding="utf-8"))
        report["files"][0]["path"] = "../escape.onnx"
        report_path.write_text(json.dumps(report), encoding="utf-8")

        try:
            prepare.load_and_validate_report(report_path)
        except prepare.PrepareTranslationError as error:
            assert "escapes export root" in str(error)
        else:
            raise AssertionError("path traversal must be rejected")


def test_destination_must_stay_under_assets() -> None:
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        unity = root / "unity"
        (unity / "Assets").mkdir(parents=True)
        try:
            prepare.validate_unity_destination(unity, Path("../outside"))
        except prepare.PrepareTranslationError:
            pass
        else:
            raise AssertionError("destination outside Assets must be rejected")


def main() -> int:
    test_prepare_copies_only_verified_report_artifacts()
    test_prepare_rejects_hash_mismatch_without_replacing_previous_assets()
    test_prepare_rejects_non_exact_parity()
    test_report_rejects_path_traversal()
    test_destination_must_stay_under_assets()
    print("PASS: local Unity translation staging is parity-gated, hash-verified, atomic, and path-safe")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
