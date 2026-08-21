#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "PhraseLayer.Core"
SCRIPTS = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts"
TESTS = ROOT / "tests" / "PhraseLayer.Core.Tests"
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
        "languagePlan.SourceText, observation.Text",
        "alignmentObservation",
    ),
    "Core Read observation pipeline",
)

require(
    CORE / "ReadEncounterStability.cs",
    (
        "enum ReadEncounterTransition",
        "sealed class ReadEncounterTracker",
        "SwitchConfirmationObservations = 2",
        "IgnoredStaleObservation",
        "sealed class ReadEncounterPipeline",
        "MixedLanguagePlan? frozenPlan",
        "decision.IsNewEncounter",
        "new ReadModeSpatialResult(frame, observation, viewportRegions, frozenPlan)",
    ),
    "Core Read encounter stability",
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
        "ReadEncounterPipeline pipeline",
        "learnerProfile.Model",
        "ocrPresenter.ObservationPresented += HandleObservationPresented",
        "pendingObservation = observation",
        "latestSequence++",
        "sequence == latestSequence",
        "encounter.Decision.IsPendingSwitch",
        "keepPreviousOverlay",
        "SpatialAssistanceCoverage.Unresolved",
        "SpatialAssistanceCoverage.Partial && !showPartialCoverage",
        "GUI.Box(ToScreenRect(target.Envelope.Value), target.Segment.DisplayText)",
    ),
    "Quest Read assistance encounter slice",
)

require(
    TESTS / "ReadObservationPipelineTests.cs",
    (
        "ExistingOcrObservationFlowsToExactSpatialAssistanceWithoutSecondOcrPass",
        "ReadModePipelineUsesSameDownstreamSpatialContractAfterOcr",
        "SpatialAssistanceCoverage.Exact",
    ),
    "Read observation pipeline regression tests",
)

require(
    TESTS / "ReadEncounterStabilityTests.cs",
    (
        "SameEncounterKeepsFrozenPlanAfterLearnerBeliefChanges",
        "OneContradictoryObservationDoesNotSwitchEncounter",
        "RepeatedContradictoryObservationConfirmsNewEncounter",
        "LongGapStartsFreshEncounterEvenForSameText",
        "StaleFrameCannotRollEncounterIdentityBackward",
        "Assert.Same(first.SpatialResult.LanguagePlan, second.SpatialResult.LanguagePlan)",
    ),
    "Read encounter stability regression tests",
)

if violations:
    raise SystemExit("\n".join(violations))

print(
    "PASS: OCR observation/frame pairs feed one downstream semantic/spatial Read pipeline; "
    "language plans are frozen per encounter with switch hysteresis and stale-frame rejection"
)
