#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import json
import tempfile
from pathlib import Path

import export_opus_mt_onnx as export


def fake_candidate() -> dict:
    return {
        "id": "opus-mt-en-jap",
        "upstream": "Helsinki-NLP/opus-mt-en-jap",
        "revision": "a863894cdd2b80f3bc1c5966734aee9ffec207d1",
        "architecture": "marian",
        "tokenization": "SentencePiece",
        "license": "Apache-2.0",
        "runtime_target": "com.unity.ai.inference@2.2.1",
        "generation_contract": {"decoder_start_token_id": 46275},
    }


def fake_inspector(path: Path) -> dict:
    return {
        "ir_version": 10,
        "opsets": [{"domain": "ai.onnx", "version": 17}],
        "inputs": [{"name": "input_ids", "element_type": 7, "shape": ["batch", "sequence"]}],
        "outputs": [{"name": "last_hidden_state", "element_type": 1, "shape": ["batch", "sequence", 512]}],
        "node_count": 3,
        "operator_counts": {"Add": 1, "MatMul": 2},
        "external_data_locations": [],
    }


def test_inventory_and_manifest_are_content_addressed() -> None:
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        (root / "encoder_model.onnx").write_bytes(b"encoder")
        (root / "decoder_model.onnx").write_bytes(b"decoder")
        (root / "source.spm").write_bytes(b"spm")

        artifacts = export.build_artifact_inventory(root, onnx_inspector=fake_inspector)
        export.validate_export_outputs(artifacts)

        by_path = {item["path"]: item for item in artifacts}
        assert by_path["encoder_model.onnx"]["sha256"] == hashlib.sha256(b"encoder").hexdigest()
        assert by_path["source.spm"]["kind"] == "support"
        assert by_path["decoder_model.onnx"]["onnx"]["inputs"][0]["name"] == "input_ids"

        manifest = export.build_manifest(fake_candidate(), artifacts)
        assert manifest["status"] == "unverified-real-unity-import-required"
        assert manifest["source"]["revision"] == "a863894cdd2b80f3bc1c5966734aee9ffec207d1"
        assert manifest["export"]["task"] == "text2text-generation"
        assert manifest["export"]["trust_remote_code"] is False

        path = export.write_manifest(root, manifest)
        parsed = json.loads(path.read_text(encoding="utf-8"))
        assert len(parsed["artifacts"]) == 3


def test_export_requires_encoder_decoder_split() -> None:
    try:
        export.validate_export_outputs(
            [{"path": "only.onnx", "kind": "onnx", "onnx": fake_inspector(Path("only.onnx"))}]
        )
    except ValueError as exception:
        assert "at least two" in str(exception)
    else:
        raise AssertionError("single ONNX artifact must not be promoted as a split encoder/decoder export")


def test_output_directory_rejects_stale_files() -> None:
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        (root / "stale.onnx").write_bytes(b"stale")
        try:
            export.ensure_empty_output_directory(root)
        except ValueError as exception:
            assert "must be empty" in str(exception)
        else:
            raise AssertionError("stale export directory must be rejected")


def main() -> int:
    test_inventory_and_manifest_are_content_addressed()
    test_export_requires_encoder_decoder_split()
    test_output_directory_rejects_stale_files()
    print("PASS: OPUS-MT export probe helpers are deterministic and fail closed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
