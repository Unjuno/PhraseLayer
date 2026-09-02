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
        assert result["schema_version"] == 2
        assert result["core_assembly_staged"] is False
        assert result["il2cpp_reflection_preserve_required"] is True
        assert result["reflection_entry_point"].endswith("MicrosoftMlMarianTokenizerFactory")
        assert all(len(item["sha256"]) == 64 for item in result["artifacts"])

        link = destination / "link.xml"
        assert link.exists() and link.stat().st_size > 0
        link_text = link.read_text(encoding="utf-8")
        for assembly in module.PRESERVED_ASSEMBLIES:
            assert f'fullname="{assembly}"' in link_text
            assert f'  <assembly fullname="{assembly}" preserve="all" />' in link_text
        descriptor = result["linker_descriptor"]
        assert descriptor["file"] == "link.xml"
        assert descriptor["preserved_assemblies"] == list(module.PRESERVED_ASSEMBLIES)
        assert descriptor["size_bytes"] == link.stat().st_size
        assert len(descriptor["sha256"]) == 64

        write(destination / "stale.dll", b"stale")
        link.write_text("stale linker", encoding="utf-8")
        second = module.stage(build, destination, manifest)
        assert not (destination / "stale.dll").exists()
        assert "stale linker" not in link.read_text(encoding="utf-8")
        assert second["linker_descriptor"]["sha256"] == result["linker_descriptor"]["sha256"]

        (build / "Google.Protobuf.dll").unlink()
        try:
            module.stage(build, destination, manifest)
        except ValueError as error:
            assert "Google.Protobuf.dll" in str(error)
        else:
            raise AssertionError("expected missing dependency validation failure")

    print("PASS: Unity tokenizer runtime staging + IL2CPP reflection preservation fixtures")


if __name__ == "__main__":
    main()
