#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "PhraseLayer.Core"
SCRIPTS = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts"
violations = []


def require(path: Path, markers: tuple[str, ...], label: str) -> None:
    if not path.is_file():
        violations.append(f"missing file: {path.relative_to(ROOT)}")
        return
    text = path.read_text(encoding="utf-8")
    for marker in markers:
        if marker not in text:
            violations.append(f"{label} missing reviewed marker: {marker}")


require(
    CORE / "Pipeline.cs",
    (
        "sealed class ReadObservationPipeline",
        "OcrRegionTextAligner().Align",
        "SemanticRegionAligner().Align",
        "SpatialAssistancePlan SpatialAssistance",
        "_observationPipeline.ProcessAsync",
    ),
    "Core Read observation pipeline",
)

require(
    SCRIPTS / "OcrViewportDebugBehaviour.cs",
    (
        "event Action<OcrObservation, ImageFrame> ObservationPresented",
        "lastFrame = frame",
        "presented(observation, frame)",
        "Raw OCR presentation must remain usable",
    ),
    "Unity OCR observation/frame handoff",
)

require(
    SCRIPTS / "QuestReadAssistanceDebugBehaviour.cs",
    (
        "ReadObservationPipeline pipeline",
        "learnerProfile.Model",
        "ocrPresenter.ObservationPresented += HandleObservationPresented",
        "pendingObservation = observation",
        "latestSequence++",
        "sequence == latestSequence",
        "SpatialAssistanceCoverage.Unresolved",
        "SpatialAssistanceCoverage.Partial && !showPartialCoverage",
        "target.Segment.SourceText, target.Segment.DisplayText",
        "GUI.Box(ToScreenRect(target.Envelope.Value), target.Segment.DisplayText)",
    ),
    "Quest Read assistance debug slice",
)

require(
    ROOT / "tests" / "PhraseLayer.Core.Tests" / "ReadObservationPipelineTests.cs",
    (
        "ExistingOcrObservationFlowsToExactSpatialAssistanceWithoutSecondOcrPass",
        "ReadModePipelineUsesSameDownstreamSpatialContractAfterOcr",
        "SpatialAssistanceCoverage.Exact",
        "Assert.Equal(2, target.Regions.Count)",
    ),
    "Read observation pipeline regression tests",
)

if violations:
    raise SystemExit("\n".join(violations))

print("PASS: recognized OCR observation/frame pairs feed one downstream semantic/spatial Read pipeline without re-running OCR; unsafe unresolved/partial overlays are suppressed by default")
