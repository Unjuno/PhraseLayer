#!/usr/bin/env python3
import importlib.util
import tempfile
from pathlib import Path

SCRIPT = Path(__file__).with_name("prepare_unity_tokenizer_runtime.py")
spec = importlib.util.spec_from_file_location("prepare_unity_tokenizer_runtime", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def write(path: Path, data: bytes) -> None:
    path.write_bytes(data)


def main() -> None:
    with tempfile.TemporaryDirectory() as temp:
        root = Path(temp)
        build = root / "build"
        destination = root / "unity"
        manifest = root / "manifest.json"
        build.mkdir()

        for name in (
            "PhraseLayer.Tokenization.Microsoft.dll",
            "Microsoft.ML.Tokenizers.dll",
            "Google.Protobuf.dll",
            "System.Memory.dll",
            "PhraseLayer.Core.dll",
        ):
            write(build / name, ("fixture:" + name).encode("utf-8"))

        result = module.stage(build, destination, manifest)
        names = {item["file"] for item in result["artifacts"]}
        assert "PhraseLayer.Core.dll" not in names
        assert {
            "PhraseLayer.Tokenization.Microsoft.dll",
            "Microsoft.ML.Tokenizers.dll",
            "Google.Protobuf.dll",
        } <= names
        assert (destination / "System.Memory.dll").exists()
        assert manifest.exists()
        assert result["core_assembly_staged"] is False
        assert all(len(item["sha256"]) == 64 for item in result["artifacts"])

        write(destination / "stale.dll", b"stale")
        module.stage(build, destination, manifest)
        assert not (destination / "stale.dll").exists()

        (build / "Google.Protobuf.dll").unlink()
        try:
            module.stage(build, destination, manifest)
        except ValueError as error:
            assert "Google.Protobuf.dll" in str(error)
        else:
            raise AssertionError("expected missing dependency validation failure")

    print("PASS: Unity tokenizer runtime staging fixtures")


if __name__ == "__main__":
    main()
