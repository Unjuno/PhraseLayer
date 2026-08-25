#!/usr/bin/env python3
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
manifest_path = UNITY / "Packages" / "manifest.json"
settings_dir = UNITY / "ProjectSettings"
version_path = settings_dir / "ProjectVersion.txt"
reference_doc = ROOT / "docs" / "UNITY_REFERENCE_BASELINE.md"

errors = []

if not manifest_path.is_file():
    errors.append("missing Unity Packages/manifest.json")
else:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    deps = manifest.get("dependencies", {})

    expected = {
        "com.unjuno.phraselayer.core": "file:../../../src/PhraseLayer.Core",
        "com.meta.xr.mrutilitykit": "85.0.0",
        "com.unity.feature.development": "1.0.2",
        "com.unity.mobile.android-logcat": "1.4.7",
        "com.unity.ai.inference": "2.2.1",
        "com.unity.ugui": "2.0.0",
        "com.unity.xr.management": "4.5.4",
        "com.unity.xr.openxr": "1.15.1",
        "com.unity.modules.ai": "1.0.0",
        "com.unity.modules.androidjni": "1.0.0",
        "com.unity.modules.imageconversion": "1.0.0",
        "com.unity.modules.imgui": "1.0.0",
        "com.unity.modules.jsonserialize": "1.0.0",
        "com.unity.modules.ui": "1.0.0",
        "com.unity.modules.uielements": "1.0.0",
        "com.unity.modules.xr": "1.0.0",
    }
    for package, version in expected.items():
        if deps.get(package) != version:
            errors.append(
                f"reference baseline drift: {package} expected {version!r}, found {deps.get(package)!r}"
            )

    for package in ("com.meta.xr.sdk.core", "com.unity.xr.meta-openxr"):
        if package in deps:
            errors.append(f"unreviewed divergence from Meta PCA baseline: explicit dependency {package}")

    for package in (
        "com.unity.modules.unityanalytics",
        "com.unity.modules.unitywebrequest",
        "com.unity.modules.unitywebrequestassetbundle",
        "com.unity.modules.unitywebrequestaudio",
        "com.unity.modules.unitywebrequesttexture",
        "com.unity.modules.unitywebrequestwww",
    ):
        if package in deps:
            errors.append(f"local-only baseline must not add runtime network/analytics module: {package}")

required_settings = (
    "ProjectVersion.txt",
    "ProjectSettings.asset",
    "EditorBuildSettings.asset",
    "EditorSettings.asset",
    "AudioManager.asset",
    "GraphicsSettings.asset",
    "DynamicsManager.asset",
    "Physics2DSettings.asset",
    "NavMeshAreas.asset",
    "MemorySettings.asset",
    "MultiplayerManager.asset",
    "PackageManagerSettings.asset",
    "PresetManager.asset",
    "TagManager.asset",
    "TimeManager.asset",
    "VersionControlSettings.asset",
    "XRPackageSettings.asset",
    "XRSettings.asset",
)
for name in required_settings:
    if not (settings_dir / name).is_file():
        errors.append(f"missing deterministic Unity project setting: ProjectSettings/{name}")

player_path = settings_dir / "ProjectSettings.asset"
if player_path.is_file():
    player = player_path.read_text(encoding="utf-8")
    for marker in (
        "serializedVersion: 28",
        "companyName: Unjuno",
        "productName: PhraseLayer",
        "Android: com.unjuno.phraselayer",
        "AndroidMinSdkVersion: 32",
        "ForceInternetPermission: 0",
        "ForceSDCardPermission: 0",
        "AndroidTargetArchitectures: 2",
        "scriptingBackend:\n    Android: 1",
    ):
        if marker not in player:
            errors.append(f"ProjectSettings.asset missing PhraseLayer Android baseline marker: {marker!r}")
    for forbidden in (
        "com.samples.passthroughcamera",
        "productName: passthroughcamera",
        "companyName: samples",
    ):
        if forbidden in player:
            errors.append(f"ProjectSettings.asset leaked Meta sample identity: {forbidden}")

build_path = settings_dir / "EditorBuildSettings.asset"
if build_path.is_file():
    build = build_path.read_text(encoding="utf-8")
    if "m_Scenes: []" not in build:
        errors.append("EditorBuildSettings.asset must keep scenes empty until the committed PhraseLayer shell scene lands")
    if "m_configObjects: {}" not in build:
        errors.append("EditorBuildSettings.asset must not reference copied Meta XR config GUIDs before those assets are intentionally imported")
    if "PassthroughCameraApiSamples" in build or re.search(r"guid: [0-9a-f]{32}", build):
        errors.append("EditorBuildSettings.asset must not reference Meta sample scenes/config GUIDs")

if not version_path.is_file():
    errors.append("missing Unity ProjectVersion.txt")
else:
    version_text = version_path.read_text(encoding="utf-8")
    if "m_EditorVersion: 6000.0.66f2" not in version_text:
        errors.append("Unity reference baseline requires 6000.0.66f2")

if not reference_doc.is_file():
    errors.append("missing docs/UNITY_REFERENCE_BASELINE.md")

if errors:
    raise SystemExit("\n".join(errors))

print(
    "Unity reference baseline PASS: Meta PCA-compatible package pins, deterministic Unity project settings, "
    "PhraseLayer Android identity/local-only permissions, and sample-GUID isolation validated"
)
