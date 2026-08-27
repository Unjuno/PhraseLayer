#!/usr/bin/env python3
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
manifest_path = UNITY / "Packages" / "manifest.json"
settings_dir = UNITY / "ProjectSettings"
version_path = settings_dir / "ProjectVersion.txt"
reference_doc = ROOT / "docs" / "UNITY_REFERENCE_BASELINE.md"
scene_path = UNITY / "Assets" / "Scenes" / "PhraseLayerReadMvp.unity"
scene_meta_path = UNITY / "Assets" / "Scenes" / "PhraseLayerReadMvp.unity.meta"
EXPECTED_SCENE_ASSET_PATH = "Assets/Scenes/PhraseLayerReadMvp.unity"
EXPECTED_SCENE_GUID = "7dc921b4703c4b5295c8de272308f789"
EXPECTED_PCA_SCRIPT_GUID = "ef9a7893e57c04c0db4114c70954b915"
EXPECTED_XR_CONFIG_GUIDS = {
    "Unity.XR.Oculus.Settings": "f2bf97b3acdb64248a707c407c9fc54e",
    "com.unity.xr.management.loader_settings": "a971eac5e950046e586c5e153e32d05c",
    "com.unity.xr.openxr.settings4": "9165b3c3dec8d446f9b11d1a99b6e245",
}
EXPECTED_OPENXR_LOADER_GUID = "648a3ff285e714febbecf3bc8c29aba6"
FORBIDDEN_SAMPLE_ANDROID_LOADER_GUID = "e5cef9052281a4476b932f9b74dcf466"
REFERENCE_XR_BLOBS = {
    "Assets/XR.meta": "96fec2d44c6d916e63d30f405fbf6684d722ee6a",
    "Assets/XR/Loaders.meta": "505c4e4076928dfd90b1d4add0c383cc4035a8c0",
    "Assets/XR/Loaders/OpenXRLoader.asset": "861fe35c9378afbe49732bcc5698f4433fbe8f4f",
    "Assets/XR/Loaders/OpenXRLoader.asset.meta": "5d793a35da551ca782e028f149ba0e83224e9a4e",
    "Assets/XR/Settings.meta": "c6afe50799013b17783496349f630847cebbd207",
    "Assets/XR/Settings/OpenXR Editor Settings.asset": "9de4c9635a9d1f2c108d65aa715ad64cb9ae041e",
    "Assets/XR/Settings/OpenXR Editor Settings.asset.meta": "cf674877838856fadeea1ba8b1dc7b4ee8e16118",
    "Assets/XR/Settings/OpenXR Package Settings.asset": "f375d5e0144d19890c49e01f3a27dc972995c688",
    "Assets/XR/Settings/OpenXR Package Settings.asset.meta": "4cf701491150c48c84c58271916420cf349161fd",
    "Assets/XR/XRGeneralSettingsPerBuildTarget.asset.meta": "c30b95fa04b45f50c5f84c476348825e30bf8852",
}
PHRASELAYER_XR_GENERAL_BLOB = "c02fc87a6a3d115a88194ab20589a57943a38dbb"
REFERENCE_STANDARD_SETTINGS_BLOBS = {
    "InputManager.asset": "8068b2058b089f9973b15f83648e58cd238688f0",
    "QualitySettings.asset": "a079bf80d077026aade1fcddfdd36d9183e6c79c",
}

errors = []


def git_blob_sha(path: Path) -> str:
    data = path.read_bytes()
    header = f"blob {len(data)}\0".encode("ascii")
    return hashlib.sha1(header + data).hexdigest()


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
    "InputManager.asset",
    "QualitySettings.asset",
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

for name, expected_sha in REFERENCE_STANDARD_SETTINGS_BLOBS.items():
    path = settings_dir / name
    if not path.is_file():
        continue
    actual_sha = git_blob_sha(path)
    if actual_sha != expected_sha:
        errors.append(
            f"standard Unity project setting drift: ProjectSettings/{name} expected reviewed blob "
            f"{expected_sha}, found {actual_sha}"
        )

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
    expected_scene_entry = (
        "  - enabled: 1\n"
        f"    path: {EXPECTED_SCENE_ASSET_PATH}\n"
        f"    guid: {EXPECTED_SCENE_GUID}"
    )
    if expected_scene_entry not in build:
        errors.append("EditorBuildSettings.asset must enable the committed PhraseLayer Read MVP scene baseline")

    for key, guid in EXPECTED_XR_CONFIG_GUIDS.items():
        marker = f"    {key}: {{fileID: 11400000, guid: {guid}, type: 2}}"
        if marker not in build:
            errors.append(f"EditorBuildSettings.asset missing reviewed XR config object: {key} -> {guid}")

    if "PassthroughCameraApiSamples" in build:
        errors.append("EditorBuildSettings.asset must not reference Meta sample scene paths")

    allowed_guids = {EXPECTED_SCENE_GUID, *EXPECTED_XR_CONFIG_GUIDS.values()}
    referenced_guids = re.findall(r"guid: ([0-9a-f]{32})", build)
    unexpected_guids = [guid for guid in referenced_guids if guid not in allowed_guids]
    if unexpected_guids:
        errors.append(
            "EditorBuildSettings.asset contains unreviewed scene/config GUIDs: " + ", ".join(unexpected_guids)
        )

for relative, expected_sha in REFERENCE_XR_BLOBS.items():
    path = UNITY / relative
    if not path.is_file():
        errors.append(f"missing reviewed XR baseline asset: {relative}")
        continue
    actual_sha = git_blob_sha(path)
    if actual_sha != expected_sha:
        errors.append(
            f"XR baseline drift: {relative} expected Meta-reference blob {expected_sha}, found {actual_sha}"
        )

xr_general_path = UNITY / "Assets" / "XR" / "XRGeneralSettingsPerBuildTarget.asset"
if not xr_general_path.is_file():
    errors.append("missing PhraseLayer XRGeneralSettingsPerBuildTarget.asset")
else:
    actual_sha = git_blob_sha(xr_general_path)
    if actual_sha != PHRASELAYER_XR_GENERAL_BLOB:
        errors.append(
            "PhraseLayer XR provider baseline drift: Assets/XR/XRGeneralSettingsPerBuildTarget.asset "
            f"expected {PHRASELAYER_XR_GENERAL_BLOB}, found {actual_sha}"
        )

    xr_general = xr_general_path.read_text(encoding="utf-8")
    for marker in (
        "m_Name: Android Providers",
        "m_Name: Android Settings",
        "m_InitManagerOnStart: 1",
        "m_AutomaticLoading: 1",
        "m_AutomaticRunning: 1",
        f"guid: {EXPECTED_OPENXR_LOADER_GUID}",
    ):
        if marker not in xr_general:
            errors.append(f"XRGeneralSettingsPerBuildTarget.asset missing Android/OpenXR marker: {marker!r}")

    if FORBIDDEN_SAMPLE_ANDROID_LOADER_GUID in xr_general:
        errors.append(
            "PhraseLayer Android XR providers must not retain the Meta sample's extra provider loader; "
            "the official runtime is OpenXR-only"
        )

    android_block = re.search(
        r"m_Name: Android Providers\s+.*?m_Loaders:\s*\n(?P<loaders>(?:\s+- \{[^\n]+\}\s*\n)*)--- !u!114",
        xr_general,
        re.DOTALL,
    )
    if android_block is None:
        errors.append("Unable to parse Android XR provider loader list")
    else:
        loader_guids = re.findall(r"guid: ([0-9a-f]{32})", android_block.group("loaders"))
        if loader_guids != [EXPECTED_OPENXR_LOADER_GUID]:
            errors.append(
                "Android XR provider list must contain exactly the reviewed OpenXR loader; found: "
                + ", ".join(loader_guids)
            )

openxr_path = UNITY / "Assets" / "XR" / "Settings" / "OpenXR Package Settings.asset"
if openxr_path.is_file():
    openxr = openxr_path.read_text(encoding="utf-8")
    meta_feature = re.search(
        r"m_Name: MetaXRFeature Android\s+.*?m_enabled: 1\s+.*?featureIdInternal: com\.meta\.openxr\.feature\.metaxr",
        openxr,
        re.DOTALL,
    )
    if meta_feature is None:
        errors.append("OpenXR Package Settings must keep MetaXRFeature enabled for Android")
    touch_feature = re.search(
        r"m_Name: OculusTouchControllerProfile Android\s+.*?m_enabled: 1\s+.*?featureIdInternal: com\.unity\.openxr\.feature\.input\.oculustouch",
        openxr,
        re.DOTALL,
    )
    if touch_feature is None:
        errors.append("OpenXR Package Settings must keep Oculus Touch controller profile enabled for Android")

openxr_editor_path = UNITY / "Assets" / "XR" / "Settings" / "OpenXR Editor Settings.asset"
if openxr_editor_path.is_file():
    openxr_editor = openxr_editor_path.read_text(encoding="utf-8")
    if openxr_editor.count("com.meta.openxr.featureset.metaxr") < 2:
        errors.append("OpenXR Editor Settings must retain the Meta XR feature set for Android and Standalone")

if not scene_path.is_file():
    errors.append("missing committed PhraseLayer Read MVP scene")
else:
    scene = scene_path.read_text(encoding="utf-8")
    if EXPECTED_PCA_SCRIPT_GUID not in scene:
        errors.append("committed Read MVP scene must serialize the reviewed MRUK 85 PassthroughCameraAccess script")
    if "PassthroughCameraApiSamples" in scene:
        errors.append("committed Read MVP scene must not copy Meta sample scene identities")

if not scene_meta_path.is_file():
    errors.append("missing committed PhraseLayer Read MVP scene meta")
else:
    scene_meta = scene_meta_path.read_text(encoding="utf-8")
    if f"guid: {EXPECTED_SCENE_GUID}" not in scene_meta:
        errors.append("PhraseLayer Read MVP scene meta GUID drifted from EditorBuildSettings.asset")

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
    "Unity reference baseline PASS: reviewed Meta XR serialized assets plus PhraseLayer's OpenXR-only Android "
    "provider lifecycle, deterministic project settings, committed Read MVP scene, local-only permissions, "
    "and sample-identity isolation validated"
)
