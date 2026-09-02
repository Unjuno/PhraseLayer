#!/usr/bin/env python3
"""Static contract for real-Unity Marian translation through ReadModeObservationProcessor."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROBE = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerMarianReadModeParityProbe.cs"
EVIDENCE = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerMarianParityEvidence.cs"
CSPROJ = ROOT / "tests/PhraseLayer.UnityMarianInferenceShell.Compile/PhraseLayer.UnityMarianInferenceShell.Compile.csproj"
COMBINED_WORKFLOW = ROOT / ".github/workflows/read-mode-marian-unity-host-gate.yml"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def forbid(text: str, fragment: str, label: str) -> None:
    if fragment in text:
        raise GateError(f"{label} contains forbidden marker: {fragment}")


def validate() -> dict[str, object]:
    probe = PROBE.read_text(encoding="utf-8")
    evidence = EVIDENCE.read_text(encoding="utf-8")
    csproj = CSPROJ.read_text(encoding="utf-8")
    workflow = COMBINED_WORKFLOW.read_text(encoding="utf-8")

    for fragment in (
        "PhraseLayerLocalMarianAssets.EncoderPath",
        "PhraseLayerLocalMarianAssets.DecoderPath",
        "PhraseLayerLocalMarianAssets.DecoderWithPastPath",
        "PhraseLayerLocalMarianAssets.ReferencePath",
        "UnityManagedMarianTokenizerLoader.TryCreateFromResources",
        "new UnityMarianDeviceResidentGenerationBackend(",
        "new OfflineSeq2SeqTranslationRuntime(tokenizer, model, options)",
        "new OfflineTranslationEngine(runtime)",
        'learner.SetUnderstanding("keep off", 0.0)',
        'new RuleBasedSemanticSegmenter(new[] { "keep off" })',
        "new LanguagePipeline(",
        "new ReadModeObservationProcessor(pipeline)",
        'new OcrObservation(\n                    "keep off"',
        'new OcrRegion("keep"',
        'new OcrRegion("off"',
        "processor.ProcessAlignedAsync(",
        "AssistancePolicy.ForMode(AssistanceMode.Balanced)",
        "ReferenceEquals(result.Spatial.Frame, frame)",
        "ReferenceEquals(result.Spatial.Observation, observation)",
        "result.Spatial.LanguagePlan.DisplayText, sample.translated_text",
        "result.Spatial.LanguagePlan.Assistance.Decisions.Count != 1",
        "result.SpatialAssistance.Targets.Count != 1",
        'target.Segment.SourceText, "keep off"',
        "target.Segment.DisplayText, sample.translated_text",
        "target.Coverage != SpatialAssistanceCoverage.Exact",
        "target.Regions.Count != 2",
        "target.Envelope == null",
        "camera_or_quest_execution=false",
    ):
        require(probe, fragment, "Marian Read Mode real-Unity parity probe")

    for forbidden in (
        "IOcrEngine",
        "MetaPassthroughCamera",
        "EnvironmentRaycastManager",
        "adb ",
        "QuestReadModeSmokeTestBehaviour",
    ):
        forbid(probe, forbidden, "Marian Read Mode parity probe")

    for fragment in (
        "PhraseLayerLocalMarianAssets.RunTranslationParityProbe()",
        "PhraseLayerMarianReadModeParityProbe.Validate()",
        '\\"schema_version\\": 2',
        '\\"read_mode_observation_processor_integration_passed\\": true',
        '\\"read_mode_exact_ocr_geometry_alignment_passed\\": true',
        '\\"camera_execution_performed\\": false',
        '\\"quest_device_execution_performed\\": false',
    ):
        require(evidence, fragment, "Marian Unity parity evidence")

    require(
        csproj,
        "PhraseLayerMarianReadModeParityProbe.cs",
        "guarded Marian compile project",
    )

    for fragment in (
        "Require real Unity exact-token Marian translation parity",
        "verify-local-marian-translation.sh",
        "PHRASELAYER_MARIAN_PARITY_EVIDENCE_PATH:",
        "Build one local-only Read Mode plus Marian Android ARM64 IL2CPP APK",
        'data["quest_device_execution_performed"] is False',
        'data["android_runtime_execution_performed"] is False',
    ):
        require(workflow, fragment, "combined Read Mode Marian host workflow")

    return {
        "status": "pass",
        "real_unity_execution_required": True,
        "real_marian_device_resident_backend_required": True,
        "read_mode_observation_processor_required": True,
        "semantic_span_assistance_required": True,
        "exact_ocr_geometry_alignment_required": True,
        "camera_execution_required": False,
        "quest_execution_required": False,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
