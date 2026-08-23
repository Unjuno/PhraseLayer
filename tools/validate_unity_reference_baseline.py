#!/usr/bin/env python3
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
manifest_path = UNITY / "Packages" / "manifest.json"
version_path = UNITY / "ProjectSettings" / "ProjectVersion.txt"
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
        "com.unity.ai.inference": "2.2.1",
        "com.unity.ugui": "2.0.0",
        "com.unity.xr.management": "4.5.4",
        "com.unity.xr.openxr": "1.15.1",
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

    # These were previously added while guessing missing transitive dependencies. The exact Meta PCA
    # reference project builds without explicitly declaring them, so keep PhraseLayer on the smaller
    # proven package surface unless a future migration records a reason to diverge.
    for package in ("com.meta.xr.sdk.core", "com.unity.xr.meta-openxr"):
        if package in deps:
            errors.append(f"unreviewed divergence from Meta PCA baseline: explicit dependency {package}")

    # PhraseLayer is local-only. Do not copy network/analytics built-in modules merely because the
    # general-purpose Meta sample includes them.
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

print("Unity reference baseline PASS: Meta PCA-compatible package pins + PhraseLayer local-only deviations validated")
