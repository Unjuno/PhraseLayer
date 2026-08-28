#!/usr/bin/env python3
import importlib.util
import json
import tempfile
from pathlib import Path

SCRIPT = Path(__file__).with_name("prepare_unity_marian_tokenizer_assets.py")
spec = importlib.util.spec_from_file_location(
    "prepare_unity_marian_tokenizer_assets",
    SCRIPT,
)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def main() -> None:
    with tempfile.TemporaryDirectory() as temp:
        root = Path(temp)
        snapshot = root / "snapshot"
        destination = root / "Resources" / "LocalTranslationAssets"
        manifest = root / "manifest.json"
        revision = "0123456789abcdef0123456789abcdef01234567"
        snapshot.mkdir()

        (snapshot / "source.spm").write_bytes(b"source fixture")
        (snapshot / "target.spm").write_bytes(b"target fixture")
        (snapshot / "vocab.json").write_text(
            json.dumps({f"token-{index}": index for index in range(46276)}),
            encoding="utf-8",
        )

        result = module.stage(snapshot, revision, destination, manifest)
        assert {item["file"] for item in result["artifacts"]} == {
            "source.spm.bytes",
            "target.spm.bytes",
            "vocab.json",
        }
        assert result["weights_staged"] is False
        assert result["revision"] == revision
        assert manifest.exists()
        assert all(len(item["sha256"]) == 64 for item in result["artifacts"])

        try:
            module.stage(snapshot, "a863894", destination, manifest)
        except ValueError as error:
            assert "40-character" in str(error)
        else:
            raise AssertionError("expected full revision validation failure")

        vocabulary = json.loads((snapshot / "vocab.json").read_text(encoding="utf-8"))
        vocabulary["duplicate"] = 0
        del vocabulary["token-46275"]
        (snapshot / "vocab.json").write_text(json.dumps(vocabulary), encoding="utf-8")
        try:
            module.stage(snapshot, revision, destination, manifest)
        except ValueError as error:
            assert "uniquely cover" in str(error)
        else:
            raise AssertionError("expected vocabulary identity validation failure")

    print("PASS: Unity Marian tokenizer asset staging fixtures")


if __name__ == "__main__":
    main()
