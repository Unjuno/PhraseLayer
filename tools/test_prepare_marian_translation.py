#!/usr/bin/env python3
import importlib.util
import json
import tempfile
from pathlib import Path

SCRIPT = Path(__file__).with_name("prepare_marian_translation.py")
spec = importlib.util.spec_from_file_location("prepare_marian_translation", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def write_json(path: Path, value) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")


def create_snapshot(root: Path) -> Path:
    snapshot = root / "snapshot"
    snapshot.mkdir()
    write_json(
        snapshot / "config.json",
        {
            "architectures": ["MarianMTModel"],
            "model_type": "marian",
            "vocab_size": 46276,
            "decoder_vocab_size": 46276,
            "d_model": 512,
            "encoder_layers": 6,
            "decoder_layers": 6,
            "max_position_embeddings": 512,
            "bos_token_id": 0,
            "eos_token_id": 0,
            "pad_token_id": 46275,
            "decoder_start_token_id": 46275,
        },
    )
    write_json(
        snapshot / "generation_config.json",
        {
            "bos_token_id": 0,
            "eos_token_id": 0,
            "forced_eos_token_id": 0,
            "pad_token_id": 46275,
            "decoder_start_token_id": 46275,
            "max_length": 512,
            "num_beams": 4,
            "renormalize_logits": True,
        },
    )
    write_json(
        snapshot / "tokenizer_config.json",
        {"source_lang": "en", "target_lang": "jap"},
    )
    write_json(snapshot / "vocab.json", {f"token-{index}": index for index in range(46276)})
    (snapshot / "source.spm").write_bytes(b"source sentencepiece fixture")
    (snapshot / "target.spm").write_bytes(b"target sentencepiece fixture")
    (snapshot / "pytorch_model.bin").write_bytes(b"weight fixture")
    return snapshot


def create_onnx(root: Path) -> Path:
    onnx = root / "onnx"
    onnx.mkdir()
    for name in module.ONNX_FILES:
        (onnx / name).write_bytes(("fixture:" + name).encode("utf-8"))
    return onnx


def expect_failure(callback, expected_fragment: str) -> None:
    try:
        callback()
    except ValueError as error:
        if expected_fragment not in str(error):
            raise AssertionError(f"expected {expected_fragment!r} in {error!r}") from error
        return
    raise AssertionError("expected validation failure")


def main() -> None:
    with tempfile.TemporaryDirectory() as temp:
        root = Path(temp)
        snapshot = create_snapshot(root)
        onnx = create_onnx(root)
        revision = "0123456789abcdef0123456789abcdef01234567"

        manifest = module.build_manifest(snapshot, revision, onnx)
        assert manifest["model_id"] == module.MODEL_ID
        assert manifest["revision"] == revision
        assert len(manifest["snapshot_artifacts"]) == len(module.SNAPSHOT_FILES)
        assert len(manifest["onnx_export"]["artifacts"]) == 3
        assert manifest["onnx_export"]["status"] == "fingerprinted"
        assert all(len(item["sha256"]) == 64 for item in manifest["snapshot_artifacts"])

        source_only = module.build_manifest(snapshot, revision, None)
        assert source_only["onnx_export"]["status"] == "not-supplied"
        assert source_only["onnx_export"]["artifacts"] == []

        expect_failure(
            lambda: module.build_manifest(snapshot, "a863894", None),
            "full lowercase 40-character Git SHA",
        )

        config_path = snapshot / "config.json"
        config = json.loads(config_path.read_text(encoding="utf-8"))
        config["vocab_size"] = 46277
        write_json(config_path, config)
        expect_failure(
            lambda: module.build_manifest(snapshot, revision, None),
            "config.vocab_size",
        )

    print("PASS: Marian translation snapshot/ONNX verifier fixtures")


if __name__ == "__main__":
    main()
