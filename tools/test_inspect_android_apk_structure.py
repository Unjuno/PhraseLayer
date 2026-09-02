#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import tempfile
import zipfile
from pathlib import Path

SCRIPT = Path(__file__).with_name("inspect_android_apk_structure.py")
spec = importlib.util.spec_from_file_location("inspect_android_apk_structure", SCRIPT)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)


def write_apk(path: Path, entries: dict[str, bytes]) -> None:
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, content in entries.items():
            archive.writestr(name, content)


def expect_failure(path: Path, message: str) -> None:
    try:
        module.inspect_apk(path)
    except module.ApkStructureError as exc:
        assert message in str(exc), (message, str(exc))
        return
    raise AssertionError("expected APK structure validation failure")


def main() -> None:
    with tempfile.TemporaryDirectory() as temp:
        root = Path(temp)
        valid = root / "valid.apk"
        write_apk(
            valid,
            {
                "AndroidManifest.xml": b"binary-manifest-fixture",
                "lib/arm64-v8a/libil2cpp.so": b"il2cpp",
                "lib/arm64-v8a/libunity.so": b"unity",
                "lib/arm64-v8a/libmain.so": b"main",
                "assets/bin/Data/data.unity3d": b"data",
                "assets/bin/Data/Managed/Metadata/global-metadata.dat": b"metadata",
            },
        )
        report = module.inspect_apk(valid)
        assert report["purpose"] == "phrase-layer-unity-android-apk-structure"
        assert report["native_abis"] == ["arm64-v8a"]
        assert report["arm64_only"] is True
        assert report["il2cpp_native_library_present"] is True
        assert report["unity_player_library_present"] is True
        assert report["native_library_count"] == 3
        assert report["unity_data_entry_count"] == 2
        assert report["runtime_execution_performed"] is False
        assert report["model_asset_presence_proven_by_zip_structure"] is False
        assert report["reflection_runtime_proven_by_zip_structure"] is False
        assert len(report["sha256"]) == 64

        wrong_abi = root / "wrong-abi.apk"
        write_apk(
            wrong_abi,
            {
                "AndroidManifest.xml": b"manifest",
                "lib/arm64-v8a/libil2cpp.so": b"il2cpp",
                "lib/arm64-v8a/libunity.so": b"unity",
                "lib/x86_64/libextra.so": b"x86",
                "assets/bin/Data/data.unity3d": b"data",
            },
        )
        expect_failure(wrong_abi, "arm64-v8a only")

        missing_il2cpp = root / "missing-il2cpp.apk"
        write_apk(
            missing_il2cpp,
            {
                "AndroidManifest.xml": b"manifest",
                "lib/arm64-v8a/libunity.so": b"unity",
                "assets/bin/Data/data.unity3d": b"data",
            },
        )
        expect_failure(missing_il2cpp, "libil2cpp.so")

        missing_data = root / "missing-data.apk"
        write_apk(
            missing_data,
            {
                "AndroidManifest.xml": b"manifest",
                "lib/arm64-v8a/libil2cpp.so": b"il2cpp",
                "lib/arm64-v8a/libunity.so": b"unity",
            },
        )
        expect_failure(missing_data, "assets/bin/Data")

        malformed = root / "malformed.apk"
        malformed.write_bytes(b"not-a-zip")
        expect_failure(malformed, "not a readable ZIP archive")

    print("PASS: Android APK arm64-only IL2CPP/Unity/data structure fixtures")


if __name__ == "__main__":
    main()
