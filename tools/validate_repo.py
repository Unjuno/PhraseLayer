#!/usr/bin/env python3
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "PhraseLayer.Core"
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
forbidden = ("using UnityEngine", "using Meta.", "using Oculus", "UnityEngine.", "OVR")
violations=[]

for path in CORE.rglob("*.cs"):
    text=path.read_text(encoding="utf-8")
    for marker in forbidden:
        if marker in text:
            violations.append(f"{path.relative_to(ROOT)}: {marker}")

manifest=json.loads((ROOT/"models"/"models.lock.json").read_text(encoding="utf-8"))
for model in manifest["candidates"]:
    if model.get("bundled") is not False:
        violations.append(f"model bundled too early: {model.get('id')}")
    for key in ("id","purpose","upstream","license","license_status","bundled"):
        if key not in model:
            violations.append(f"model missing {key}: {model}")

required_unity = [
    UNITY / "ProjectSettings" / "ProjectVersion.txt",
    UNITY / "Packages" / "manifest.json",
    UNITY / "Assets" / "PhraseLayer.Unity.asmdef",
    UNITY / "Assets" / "Scripts" / "PhraseLayerDemoBehaviour.cs",
    UNITY / "Assets" / "Scripts" / "UnityTextureFramePayload.cs",
    UNITY / "Assets" / "Scripts" / "MetaPassthroughCameraBridge.cs",
    UNITY / "Assets" / "Editor" / "PhraseLayerEditorVerification.cs",
]
for path in required_unity:
    if not path.exists():
        violations.append(f"missing Unity shell file: {path.relative_to(ROOT)}")

core_package=json.loads((CORE/"package.json").read_text(encoding="utf-8"))
if core_package.get("name") != "com.unjuno.phraselayer.core":
    violations.append("unexpected Core UPM package name")

core_asmdef=json.loads((CORE/"PhraseLayer.Core.asmdef").read_text(encoding="utf-8"))
if core_asmdef.get("noEngineReferences") is not True:
    violations.append("PhraseLayer.Core.asmdef must set noEngineReferences=true")

unity_manifest=json.loads((UNITY/"Packages"/"manifest.json").read_text(encoding="utf-8"))
deps=unity_manifest.get("dependencies", {})
expected_packages = {
    "com.unjuno.phraselayer.core": "file:../../src/PhraseLayer.Core",
    "com.meta.xr.mrutilitykit": "85.0.0",
    "com.unity.ai.inference": "2.2.1",
    "com.unity.xr.management": "4.5.4",
    "com.unity.xr.openxr": "1.15.1",
    "com.unity.ugui": "2.0.0",
}
for package, expected in expected_packages.items():
    actual=deps.get(package)
    if actual != expected:
        violations.append(f"Unity package drift: {package} expected {expected} but found {actual}")

project_version=(UNITY/"ProjectSettings"/"ProjectVersion.txt").read_text(encoding="utf-8")
if "m_EditorVersion: 6000.0.66f2" not in project_version:
    violations.append("Unity editor pin must remain 6000.0.66f2 until the Meta baseline is intentionally updated")

if violations:
    raise SystemExit("\n".join(violations))

print(
    f"PASS: {len(list(CORE.rglob('*.cs')))} core files; boundaries, model manifest, "
    "Unity shell, Meta baseline package pins, and camera adapter structure validated"
)
