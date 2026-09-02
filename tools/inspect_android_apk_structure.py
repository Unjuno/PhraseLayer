#!/usr/bin/env python3
"""Inspect a locally built Unity Android APK without executing or redistributing it.

This is a packaging-structure gate, not a runtime gate. It proves that the APK is a readable archive, carries
Unity IL2CPP native libraries for arm64-v8a only, and contains Unity player data. It deliberately does not claim
that Marian model assets, reflection, GPU inference or the Android runtime execute correctly; those remain covered
by separate Unity parity/build/runtime gates.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import zipfile
from typing import Iterable


class ApkStructureError(ValueError):
    pass


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _native_abis(names: Iterable[str]) -> list[str]:
    abis: set[str] = set()
    for name in names:
        parts = name.split("/")
        if len(parts) >= 3 and parts[0] == "lib" and parts[-1].endswith(".so"):
            abis.add(parts[1])
    return sorted(abis)


def inspect_apk(path: pathlib.Path) -> dict[str, object]:
    if not path.is_file() or path.stat().st_size <= 0:
        raise ApkStructureError(f"APK is missing or empty: {path}")
    if not zipfile.is_zipfile(path):
        raise ApkStructureError(f"APK is not a readable ZIP archive: {path}")

    try:
        with zipfile.ZipFile(path, "r") as archive:
            infos = archive.infolist()
            names = [info.filename for info in infos if not info.is_dir()]
            bad = archive.testzip()
            if bad is not None:
                raise ApkStructureError(f"APK contains a corrupt ZIP member: {bad}")
    except (OSError, zipfile.BadZipFile) as exc:
        raise ApkStructureError(f"failed to inspect APK archive: {exc}") from exc

    name_set = set(names)
    if "AndroidManifest.xml" not in name_set:
        raise ApkStructureError("APK is missing AndroidManifest.xml")

    abis = _native_abis(names)
    if abis != ["arm64-v8a"]:
        raise ApkStructureError(
            "Marian product fixture APK must contain native libraries for arm64-v8a only; observed="
            + ",".join(abis or ["none"])
        )

    il2cpp = "lib/arm64-v8a/libil2cpp.so"
    unity = "lib/arm64-v8a/libunity.so"
    if il2cpp not in name_set:
        raise ApkStructureError(f"APK is missing required IL2CPP library: {il2cpp}")
    if unity not in name_set:
        raise ApkStructureError(f"APK is missing required Unity player library: {unity}")

    data_entries = sorted(name for name in names if name.startswith("assets/bin/Data/"))
    if not data_entries:
        raise ApkStructureError("APK has no Unity assets/bin/Data payload")

    native_entries = sorted(name for name in names if name.startswith("lib/") and name.endswith(".so"))
    return {
        "schema_version": 1,
        "purpose": "phrase-layer-unity-android-apk-structure",
        "file_name": path.name,
        "size_bytes": path.stat().st_size,
        "sha256": sha256_file(path),
        "zip_integrity_passed": True,
        "android_manifest_present": True,
        "native_abis": abis,
        "arm64_only": True,
        "il2cpp_native_library_present": True,
        "unity_player_library_present": True,
        "native_library_count": len(native_entries),
        "unity_data_entry_count": len(data_entries),
        "runtime_execution_performed": False,
        "model_asset_presence_proven_by_zip_structure": False,
        "reflection_runtime_proven_by_zip_structure": False,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--apk", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()

    report = inspect_apk(args.apk)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(report, sort_keys=True))


if __name__ == "__main__":
    main()
