#!/usr/bin/env python3
"""Static safety/diagnostic contract for the real Quest Read Mode smoke harness.

The actual PASS still requires a headset. This gate prevents host-side changes from reintroducing synthetic OCR
false positives, allowing OCR-only success to masquerade as Read Mode success, or leaking recognized text in the
end-to-end report.
"""

from __future__ import annotations

import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
OCR = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/QuestOcrSmokeTestBehaviour.cs"
READ = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/QuestReadModeSmokeTestBehaviour.cs"
RUNTIME = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/OcrDebugRuntimeBehaviour.cs"
PRESENTER = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/OcrViewportDebugBehaviour.cs"
SETUP = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerEditorSetup.cs"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def validate() -> dict[str, object]:
    ocr = OCR.read_text(encoding="utf-8")
    read = READ.read_text(encoding="utf-8")
    runtime = RUNTIME.read_text(encoding="utf-8")
    presenter = PRESENTER.read_text(encoding="utf-8")
    setup = SETUP.read_text(encoding="utf-8")

    for fragment in (
        "presenter.LoadSyntheticFixtureOnStart = false",
        "presenter.Clear()",
        "var previousAutoRun = runtimeDriver.AutoRun",
        "runtimeDriver.AutoRun = false",
        "presenter.Regions.Count >= minimumRecognizedRegions",
        '"recognizer=unobserved"',
        'recognized_text=<redacted; enable includeRecognizedTextInReport explicitly>',
        "timeoutCancellation.CancelAfter",
    ):
        require(ocr, fragment, "Quest OCR smoke")

    require(runtime, "public bool AutoRun", "OCR runtime")
    require(presenter, "public bool LoadSyntheticFixtureOnStart", "OCR presenter")

    for fragment in (
        "await ocrSmoke.RunSmokeTestAsync(runToken)",
        "liveReadMode.ProcessedObservationCount > processedBefore",
        "projection.LastWorldTextLayout.ReadyCount >= minimumObservedTracks",
        "plan.ObservedCount >= minimumObservedTracks",
        "worldTextTracking.LastMaskSucceeded",
        "worldTextTracking.LastRenderSucceeded",
        "mask.ActiveMaskCount >= minimumActiveMasks",
        "renderer.ActiveViewCount >= minimumRenderedViews",
        "recognized_text=<redacted>",
        "display_text=<redacted>",
        "max_observed_planarity_error_m=",
    ):
        require(read, fragment, "Quest Read Mode smoke")

    for fragment in (
        "root.AddComponent<QuestOcrSmokeTestBehaviour>()",
        "root.AddComponent<QuestReadModeSmokeTestBehaviour>()",
        "questOcrSmoke.SetSceneReferences(runtimeDriver, presenter, ocrBootstrap)",
        "questReadModeSmoke.SetSceneReferences(questOcrSmoke, liveReadMode, worldTextTracking)",
    ):
        require(setup, fragment, "demo scene setup")

    return {
        "status": "pass",
        "synthetic_ocr_can_pass": False,
        "ocr_region_required": True,
        "recognizer_runtime_observation_required": True,
        "world_surface_required": True,
        "current_track_required": True,
        "source_mask_required": True,
        "world_text_render_required": True,
        "recognized_text_redacted_by_default": True,
        "quest_device_run_still_required": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
