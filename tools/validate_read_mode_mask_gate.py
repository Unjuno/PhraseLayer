#!/usr/bin/env python3
"""Static regression gate for conservative Read Mode source masking.

This does not validate passthrough appearance. It prevents the implementation from silently regressing to
retained/stale physical masks, collider-backed virtual masks, or an unwired source-mask component before the
Quest visual gate is run.
"""

from __future__ import annotations

import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
CORE = ROOT / "src/PhraseLayer.Core/WorldTextMasking.cs"
UNITY = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/UnityWorldTextSourceMaskBehaviour.cs"
TRACKING = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/UnityWorldTextTrackingBehaviour.cs"
SETUP = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerEditorSetup.cs"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def forbid(text: str, fragment: str, label: str) -> None:
    if fragment in text:
        raise GateError(f"{label} contains forbidden marker: {fragment}")


def validate() -> dict[str, object]:
    core = CORE.read_text(encoding="utf-8")
    unity = UNITY.read_text(encoding="utf-8")
    tracking = TRACKING.read_text(encoding="utf-8")
    setup = SETUP.read_text(encoding="utf-8")

    for fragment in (
        "NotObservedThisFrame",
        "InsufficientObservations",
        "NoVisibleReplacement",
        "ExcessivePlanarityError",
        "if (!track.ObservedThisFrame)",
        "track.ObservationCount < MinimumObservationCount",
        "track.Surface.MaxPlanarityErrorMeters > MaximumPlanarityErrorMeters",
    ):
        require(core, fragment, "Core mask policy")

    for fragment in (
        "UnityWorldTextSourceMaskBehaviour",
        "new WorldTextMaskPolicy(minimumObservationCount, maximumPlanarityErrorMeters)",
        "new Mesh()",
        "meshFilter.sharedMesh = mesh",
        "maskMaterial",
        "LastEligibleMaskCount",
        "LastSuppressedMaskCount",
    ):
        require(unity, fragment, "Unity source mask")

    for fragment in ("CreatePrimitive", "AddComponent<Collider", "AddComponent<MeshCollider"):
        forbid(unity, fragment, "Unity source mask")

    require(tracking, "UnityWorldTextSourceMaskBehaviour", "world text tracking")
    require(tracking, "sourceMask.TryPresent(LastPlan)", "world text tracking")
    require(setup, "root.AddComponent<UnityWorldTextSourceMaskBehaviour>()", "demo scene setup")
    require(setup, "worldTextTracking.SetSourceMask(worldTextSourceMask)", "demo scene setup")
    require(setup, "reviewed opaque source-mask Material", "demo scene setup")

    return {
        "status": "pass",
        "mask_retained_tracks": False,
        "minimum_observation_gate": True,
        "stricter_planarity_gate": True,
        "procedural_mesh_has_collider": False,
        "demo_scene_wired": True,
        "quest_visual_validation_still_required": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
