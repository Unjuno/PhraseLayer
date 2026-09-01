#!/usr/bin/env python3
"""Stage locally built managed SentencePiece runtime assemblies into the Unity project.

This tool never downloads packages. Build PhraseLayer.Tokenization.Microsoft with the reviewed
NuGet lock first, then point --build-output at that project's output directory. The entire managed
dependency closure present beside the adapter is copied except PhraseLayer.Core.dll, because Core is
already supplied to Unity as the local com.unjuno.phraselayer.core package.

The Unity bridge resolves PhraseLayer.Tokenization.Microsoft by reflection, so IL2CPP/linker stripping
must not be allowed to erase that entry point. Staging therefore also writes a narrow link.xml preserving
the reflection entry assembly plus its reviewed tokenizer/protobuf runtime dependencies.

Real Unity import and Quest execution remain separate gates.
"""

import argparse
import hashlib
import json
import shutil
from pathlib import Path

REQUIRED = {
    "PhraseLayer.Tokenization.Microsoft.dll",
    "Microsoft.ML.Tokenizers.dll",
    "Google.Protobuf.dll",
}
EXCLUDED = {
    "PhraseLayer.Core.dll",
}
PRESERVED_ASSEMBLIES = (
    "PhraseLayer.Tokenization.Microsoft",
    "Microsoft.ML.Tokenizers",
    "Google.Protobuf",
)
LINK_XML_NAME = "link.xml"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _write_linker_descriptor(destination: Path) -> dict:
    lines = ["<linker>"]
    for assembly in PRESERVED_ASSEMBLIES:
        lines.append(f'  <assembly fullname="{assembly}" preserve="all" />')
    lines.append("</linker>")
    path = destination / LINK_XML_NAME
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return {
        "file": LINK_XML_NAME,
        "size_bytes": path.stat().st_size,
        "sha256": sha256(path),
        "preserved_assemblies": list(PRESERVED_ASSEMBLIES),
    }


def stage(build_output: Path, destination: Path, manifest_path: Path) -> dict:
    if not build_output.is_dir():
        raise ValueError(f"build output directory does not exist: {build_output}")

    dlls = sorted(
        path for path in build_output.glob("*.dll")
        if path.name not in EXCLUDED
    )
    names = {path.name for path in dlls}
    missing = sorted(REQUIRED - names)
    if missing:
        raise ValueError(
            "missing required managed tokenizer assemblies: " + ", ".join(missing)
        )

    destination.mkdir(parents=True, exist_ok=True)
    for previous in destination.glob("*.dll"):
        previous.unlink()
    linker_path = destination / LINK_XML_NAME
    if linker_path.exists():
        linker_path.unlink()

    artifacts = []
    for source in dlls:
        target = destination / source.name
        shutil.copy2(source, target)
        artifacts.append(
            {
                "file": source.name,
                "size_bytes": target.stat().st_size,
                "sha256": sha256(target),
            }
        )

    linker_descriptor = _write_linker_descriptor(destination)
    manifest = {
        "schema_version": 2,
        "source": str(build_output),
        "destination": str(destination),
        "runtime": "Microsoft.ML.Tokenizers",
        "artifacts": artifacts,
        "core_assembly_staged": False,
        "reflection_entry_point": "PhraseLayer.Tokenization.Microsoft.MicrosoftMlMarianTokenizerFactory",
        "il2cpp_reflection_preserve_required": True,
        "linker_descriptor": linker_descriptor,
        "unity_compatibility": "unverified-real-unity-import-required",
    }
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(
        json.dumps(manifest, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--build-output", type=Path, required=True)
    parser.add_argument(
        "--destination",
        type=Path,
        default=Path("unity/PhraseLayer.Unity/Assets/LocalTokenizerRuntime"),
    )
    parser.add_argument(
        "--manifest",
        type=Path,
        default=Path("artifacts/tokenizer-runtime/unity-tokenizer-runtime.manifest.json"),
    )
    args = parser.parse_args()

    manifest = stage(args.build_output, args.destination, args.manifest)
    print(
        "PASS: staged "
        f"{len(manifest['artifacts'])} managed tokenizer assemblies + IL2CPP linker descriptor; manifest={args.manifest}"
    )


if __name__ == "__main__":
    main()
