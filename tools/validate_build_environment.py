#!/usr/bin/env python3
from __future__ import annotations

import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
LOCK = ROOT / "ci" / "unity-environment.lock.json"
GLOBAL_JSON = ROOT / "global.json"
PROJECT_VERSION = ROOT / "unity" / "PhraseLayer.Unity" / "ProjectSettings" / "ProjectVersion.txt"
MANIFEST = ROOT / "unity" / "PhraseLayer.Unity" / "Packages" / "manifest.json"
PACKAGES_LOCK = ROOT / "unity" / "PhraseLayer.Unity" / "Packages" / "packages-lock.json"
EDITOR_CSPROJ = ROOT / "tests" / "PhraseLayer.UnityShell.Compile" / "PhraseLayer.UnityShell.Compile.csproj"
ANDROID_CSPROJ = ROOT / "tests" / "PhraseLayer.UnityShell.Compile" / "PhraseLayer.UnityAndroid.Compile.csproj"

errors: list[str] = []
warnings: list[str] = []


def require(condition: bool, message: str) -> None:
    if not condition:
        errors.append(message)


def load_json(path: Path) -> dict:
    if not path.is_file():
        errors.append(f"missing build-environment file: {path.relative_to(ROOT)}")
        return {}
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception as exc:
        errors.append(f"invalid JSON in {path.relative_to(ROOT)}: {exc}")
        return {}


def read(path: Path) -> str:
    if not path.is_file():
        errors.append(f"missing build-environment file: {path.relative_to(ROOT)}")
        return ""
    return path.read_text(encoding="utf-8")


def main() -> int:
    env = load_json(LOCK)
    global_json = load_json(GLOBAL_JSON)
    manifest = load_json(MANIFEST)
    project_version = read(PROJECT_VERSION)
    editor_csproj = read(EDITOR_CSPROJ)
    android_csproj = read(ANDROID_CSPROJ)

    require(env.get("schema_version") == 1, "unity environment lock schema_version must be 1")

    unity = env.get("unity", {})
    expected_editor = unity.get("editor_version")
    require(
        f"m_EditorVersion: {expected_editor}" in project_version,
        f"ProjectVersion.txt must pin Unity {expected_editor}",
    )
    require(unity.get("build_target") == "Android", "reference build target must remain Android")
    require(unity.get("csharp_language_version") == "9.0", "Unity C# language contract must remain 9.0")
    require(unity.get("api_compatibility") == "netstandard2.1", "Unity API compatibility mirror must remain netstandard2.1")

    expected_sdk = env.get("host_preflight", {}).get("dotnet_sdk")
    require(global_json.get("sdk", {}).get("version") == expected_sdk, f"global.json must pin .NET SDK {expected_sdk}")
    require(global_json.get("sdk", {}).get("rollForward") == "disable", "global.json must disable SDK roll-forward for deterministic CI")

    expected_packages = env.get("direct_packages", {})
    actual_packages = manifest.get("dependencies", {})
    require(actual_packages == expected_packages, "Packages/manifest.json direct dependencies drifted from ci/unity-environment.lock.json")
    require(manifest.get("enableLockFile", True) is not False, "Unity Package Manager lock file must remain enabled")

    for label, text, defines in (
        ("Editor", editor_csproj, env.get("host_preflight", {}).get("editor_defines", [])),
        ("Android", android_csproj, env.get("host_preflight", {}).get("android_defines", [])),
    ):
        require("<TargetFramework>netstandard2.1</TargetFramework>" in text, f"{label} preflight must target netstandard2.1")
        require("<LangVersion>9.0</LangVersion>" in text, f"{label} preflight must target C# 9.0")
        for define in defines:
            require(define in text, f"{label} preflight missing define {define}")

    if PACKAGES_LOCK.is_file():
        resolved = load_json(PACKAGES_LOCK).get("dependencies", {})
        for package, version in expected_packages.items():
            if package == "com.unjuno.phraselayer.core":
                require(package in resolved, "packages-lock.json must include the local PhraseLayer Core package")
                continue
            require(package in resolved, f"packages-lock.json missing direct package {package}")
            if package in resolved:
                require(
                    str(resolved[package].get("version", "")) == version,
                    f"packages-lock.json resolved {package}={resolved[package].get('version')} but environment lock expects {version}",
                )
    else:
        warnings.append(
            "Packages/packages-lock.json is not committed yet. Capture it from the first reviewed real Unity/UBA package resolution; do not fabricate transitive dependencies."
        )

    forbidden_names = {"UnityEntitlementLicense.xml"}
    forbidden_suffixes = {".ulf", ".alf"}
    for path in ROOT.rglob("*"):
        if ".git" in path.parts or not path.is_file():
            continue
        if path.name in forbidden_names or path.suffix.lower() in forbidden_suffixes:
            errors.append(f"Unity license material must never be committed: {path.relative_to(ROOT)}")

    for warning in warnings:
        print(f"WARNING: {warning}")
    if errors:
        for error in errors:
            print(f"ERROR: {error}")
        return 1

    print("PASS: repository build environment matches the pinned Unity/Android/package/C#/.NET compile contract")
    return 0


if __name__ == "__main__":
    sys.exit(main())
