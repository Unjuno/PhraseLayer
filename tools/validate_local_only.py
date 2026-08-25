#!/usr/bin/env python3
"""Validate the privacy/local-only contract of the official PhraseLayer runtime.

This validator intentionally does not ban replaceable engine interfaces. It bans concrete runtime
network dependencies in the official Core/Quest implementation so future community/provider adapters
can live outside the reference distribution without changing the product contract. It also pins the
Quest Android manifest boundary because camera access is allowed while network access is not.
"""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "PhraseLayer.Core"
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
RUNTIME_ROOTS = (CORE, UNITY / "Assets" / "Scripts")

FORBIDDEN_RUNTIME_MARKERS = (
    "UnityEngine.Networking.",
    "System.Net.",
    "HttpClient",
    "WebClient",
    "WebRequest.Create",
    "TcpClient",
    "UdpClient",
)

FORBIDDEN_DIRECT_PACKAGES = (
    "com.unity.services.analytics",
    "com.unity.services.authentication",
    "com.unity.services.cloudcode",
    "com.unity.services.remote-config",
)

FORBIDDEN_MANIFEST_PERMISSIONS = (
    "android.permission.INTERNET",
    "android.permission.ACCESS_NETWORK_STATE",
)

QUEST_MANIFEST_MARKERS = (
    'android:name="android.permission.CAMERA"',
    'android:name="horizonos.permission.HEADSET_CAMERA"',
    'android:name="com.oculus.feature.PASSTHROUGH"',
    'android:name="android.hardware.vr.headtracking"',
    'android:name="com.oculus.supportedDevices"',
    'android:value="quest3|quest3s"',
    'android:name="com.unity3d.player.UnityPlayerGameActivity"',
)

PLAYER_SETTINGS_MARKERS = (
    "AndroidMinSdkVersion: 32",
    "AndroidTargetSdkVersion: 36",
    "AndroidTargetArchitectures: 2",
    "androidApplicationEntry: 2",
    "useCustomMainManifest: 1",
    "ForceInternetPermission: 0",
    "ForceSDCardPermission: 0",
)

violations: list[str] = []


def require_file(path: Path) -> str:
    if not path.is_file():
        violations.append(f"missing file: {path.relative_to(ROOT)}")
        return ""
    return path.read_text(encoding="utf-8")


def is_editor_only(path: Path) -> bool:
    return "Editor" in path.parts


for runtime_root in RUNTIME_ROOTS:
    if not runtime_root.is_dir():
        violations.append(f"missing runtime root: {runtime_root.relative_to(ROOT)}")
        continue
    for path in runtime_root.rglob("*.cs"):
        if is_editor_only(path):
            continue
        text = path.read_text(encoding="utf-8")
        for marker in FORBIDDEN_RUNTIME_MARKERS:
            if marker in text:
                violations.append(
                    f"{path.relative_to(ROOT)} contains forbidden runtime networking marker {marker!r}"
                )

manifest_path = UNITY / "Packages" / "manifest.json"
manifest_text = require_file(manifest_path)
if manifest_text:
    manifest = json.loads(manifest_text)
    dependencies = manifest.get("dependencies", {})
    for package in FORBIDDEN_DIRECT_PACKAGES:
        if package in dependencies:
            violations.append(
                f"official Unity project must not depend on cloud/network service package: {package}"
            )

assets_root = UNITY / "Assets"
if assets_root.is_dir():
    for path in assets_root.rglob("AndroidManifest.xml"):
        text = path.read_text(encoding="utf-8")
        for permission in FORBIDDEN_MANIFEST_PERMISSIONS:
            if permission.lower() in text.lower():
                violations.append(
                    f"{path.relative_to(ROOT)} requests forbidden network permission {permission}"
                )

quest_manifest = require_file(UNITY / "Assets" / "Plugins" / "Android" / "AndroidManifest.xml")
if quest_manifest:
    for marker in QUEST_MANIFEST_MARKERS:
        if marker not in quest_manifest:
            violations.append(f"Quest Android manifest missing reviewed marker: {marker}")
    if "com.oculus.telemetry.project_guid" in quest_manifest:
        violations.append("Quest Android manifest must not copy the Meta sample telemetry project GUID")
    for permission in ("com.oculus.permission.USE_SCENE", "com.oculus.permission.USE_ANCHOR_API"):
        if permission in quest_manifest:
            violations.append(
                f"committed model-free Read baseline must not request unused Meta permission {permission}"
            )

player_settings = require_file(UNITY / "ProjectSettings" / "ProjectSettings.asset")
if player_settings:
    for marker in PLAYER_SETTINGS_MARKERS:
        if marker not in player_settings:
            violations.append(f"Android PlayerSettings missing reviewed Quest build marker: {marker}")

build_guard = require_file(UNITY / "Assets" / "Editor" / "PhraseLayerLocalOnlyBuildGuard.cs")
for marker in (
    "PlayerSettings.Android.forceInternetPermission",
    "PlayerSettings.Android.forceSDCardPermission = false",
    "PhraseLayer local-only contract failed",
    "IPreprocessBuildWithReport",
    "ForbiddenRuntimeNetworkMarkers",
):
    if build_guard and marker not in build_guard:
        violations.append(f"local-only Unity build guard missing reviewed marker: {marker}")

translation = require_file(CORE / "Translation.cs")
if translation:
    if "interface ITranslationEngine" not in translation:
        violations.append("ITranslationEngine replaceable boundary was removed")
    if "DictionaryTranslationEngine" not in translation:
        violations.append("reference local translation implementation is missing")

inputs = require_file(CORE / "Inputs.cs")
if inputs:
    for marker in ("interface IOcrEngine", "interface IAsrEngine"):
        if marker not in inputs:
            violations.append(f"replaceable local runtime boundary missing: {marker}")

privacy_doc = require_file(ROOT / "docs" / "LOCAL_ONLY.md")
for marker in (
    "Local is the reference runtime",
    "No PhraseLayer backend",
    "No automatic cloud fallback",
    "Provider interfaces remain replaceable",
):
    if privacy_doc and marker not in privacy_doc:
        violations.append(f"docs/LOCAL_ONLY.md missing reviewed marker: {marker}")

if violations:
    raise SystemExit("\n".join(violations))

print(
    "PASS: official PhraseLayer runtime contains no reviewed network APIs/permissions/service packages; "
    "Quest build manifest declares only reviewed camera/MR capabilities, Android build settings are pinned, "
    "and OCR/ASR/translation interfaces remain replaceable"
)
