#!/usr/bin/env python3
"""Static contract for Quest Read Mode physical placement through MRUK live environment depth."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
MANIFEST = UNITY / "Packages" / "manifest.json"
ADAPTER = UNITY / "Assets" / "Scripts" / "UnityEnvironmentSurfaceRaycaster.cs"
PROJECTION = UNITY / "Assets" / "Scripts" / "UnitySpatialProjection.cs"
SETUP = UNITY / "Assets" / "Editor" / "PhraseLayerEditorSetup.cs"
SMOKE = UNITY / "Assets" / "Scripts" / "QuestReadModeSmokeTestBehaviour.cs"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def validate() -> dict[str, object]:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    dependencies = manifest.get("dependencies", {})
    if dependencies.get("com.meta.xr.mrutilitykit") != "85.0.0":
        raise GateError("MRUK must remain pinned to 85.0.0 for this reviewed environment-raycast gate")
    if dependencies.get("com.unity.xr.openxr") != "1.15.1":
        raise GateError("Unity OpenXR must remain pinned to 1.15.1")
    if dependencies.get("com.unity.xr.meta-openxr") != "2.2.1":
        raise GateError("Unity OpenXR: Meta must remain pinned to 2.2.1")

    adapter = ADAPTER.read_text(encoding="utf-8")
    for fragment in (
        "class UnityEnvironmentSurfaceRaycaster : MonoBehaviour, ISurfaceRaycaster",
        'ExpectedManagerTypeName = "Meta.XR.EnvironmentRaycastManager"',
        'GetProperty("IsSupported")',
        'candidate.Name, "Raycast"',
        "parameters[0].ParameterType != typeof(Ray)",
        "parameters[1].IsOut",
        "parameters[2].ParameterType != typeof(float)",
        'FindRequiredMember(hitType, "point")',
        'FindRequiredMember(hitType, "normal")',
        'FindOptionalMember(hitType, "normalConfidence")',
        "return false;",
    ):
        require(adapter, fragment, "MRUK environment adapter")

    projection = PROJECTION.read_text(encoding="utf-8")
    for fragment in (
        "UnityEnvironmentSurfaceRaycaster environmentSurfaceRaycaster",
        "public bool UsesEnvironmentRaycast",
        "SetSceneReferences(\n            MetaPassthroughCameraBridge viewportRayProvider,\n            UnityEnvironmentSurfaceRaycaster worldSurfaceRaycaster)",
        "if (environmentSurfaceRaycaster != null)",
        "return environmentSurfaceRaycaster",
    ):
        require(projection, fragment, "Unity spatial projection")

    setup = SETUP.read_text(encoding="utf-8")
    for fragment in (
        'MetaEnvironmentRaycastManagerTypeName = "Meta.XR.EnvironmentRaycastManager"',
        "root.AddComponent<UnityEnvironmentSurfaceRaycaster>()",
        "AddRequiredMetaComponent(root, MetaEnvironmentRaycastManagerTypeName)",
        "environmentSurfaceRaycaster.SetEnvironmentRaycastManager(environmentRaycastManager)",
        "spatialProjection.SetSceneReferences(cameraBridge, environmentSurfaceRaycaster)",
    ):
        require(setup, fragment, "Quest demo scene setup")
    if "root.AddComponent<UnityPhysicsSurfaceRaycaster>()" in setup:
        raise GateError("Quest demo scene must not silently depend on generated Physics colliders")

    smoke = SMOKE.read_text(encoding="utf-8")
    for fragment in (
        "projection.UsesEnvironmentRaycast",
        "projection.EnvironmentSurfaceRaycaster.AbiValidated",
        '"MRUKEnvironmentRaycast"',
        "last_normal_confidence=",
    ):
        require(smoke, fragment, "Quest Read Mode smoke")

    return {
        "status": "pass",
        "mruk_version": "85.0.0",
        "openxr_version": "1.15.1",
        "meta_openxr_version": "2.2.1",
        "scene_scan_required": False,
        "physics_collider_required": False,
        "live_depth_environment_raycast_required": True,
        "mruk_abi_checked_at_runtime": True,
        "quest_device_validation_still_required": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
