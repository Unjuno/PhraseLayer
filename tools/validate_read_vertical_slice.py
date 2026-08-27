#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "PhraseLayer.Core"
UNITY = ROOT / "unity" / "PhraseLayer.Unity" / "Assets"
SCRIPTS = UNITY / "Scripts"
EDITOR = UNITY / "Editor"
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
        "if (plan == null)",
        "new ReadModeSpatialResult(frame, observation, viewportRegions, plan)",
    ),
    "Core Read encounter stability",
)

require(
    CORE / "ViewportEnvelopeStabilizer.cs",
    (
        "sealed class ViewportEnvelopeStabilizerOptions",
        "BlendFactor = 0.35",
        "ResetCenterDistance = 0.10",
        "sealed class ViewportEnvelopeStabilizer",
        "centerDistance > options.ResetCenterDistance",
        "states.Clear()",
    ),
    "Core viewport overlay stabilization",
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
        "ITranslationEngine configuredTranslationEngine",
        "ViewportEnvelopeStabilizer overlayStabilizer",
        "UpdateStabilizedEnvelopes(encounter.Decision.EncounterId, result)",
        "stabilizedEnvelopes.TryGetValue(target.Segment.Unit.Id",
        "ResetOverlayStability();",
        "ConfigureTranslationEngine(ITranslationEngine engine)",
        "pipeline.Reset();",
        "translationEngine = configuredTranslationEngine",
        "new DictionaryTranslationEngine(translations)",
        "learnerProfile.Model",
        "ocrPresenter.ObservationPresented += HandleObservationPresented",
        "pendingObservation = observation",
        "latestSequence++",
        "sequence == latestSequence",
        "encounter.Decision.IsPendingSwitch",
        "keepPreviousOverlay",
        "SpatialAssistanceCoverage.Unresolved",
        "SpatialAssistanceCoverage.Partial && !showPartialCoverage",
        "GUI.Box(ToScreenRect(envelope), target.Segment.DisplayText)",
    ),
    "Quest Read assistance encounter slice",
)

require(
    SCRIPTS / "UnityXrHeadPoseBehaviour.cs",
    (
        "InputDevices.GetDeviceAtXRNode(XRNode.Head)",
        "CommonUsages.devicePosition",
        "CommonUsages.deviceRotation",
        "transform.localPosition = position",
        "transform.localRotation = rotation",
    ),
    "Unity XR head-pose driver",
)

require(
    SCRIPTS / "PhraseLayerReadMvpRuntimeInstaller.cs",
    (
        "InstallHeadTracking();",
        'MainCameraObjectName = "Main Camera"',
        "camera.transform.localPosition = Vector3.zero",
        "camera.transform.localRotation = Quaternion.identity",
        "camera.gameObject.AddComponent<UnityXrHeadPoseBehaviour>()",
    ),
    "Committed Read MVP runtime installer head tracking",
)

require(
    UNITY / "Scenes" / "PhraseLayerReadMvp.unity",
    (
        "m_Name: Main Camera",
        "m_LocalPosition: {x: 0, y: 0, z: 0}",
        "m_TagString: MainCamera",
    ),
    "Committed Read MVP camera origin",
)

require(
    EDITOR / "PhraseLayerReadMvpSceneSetup.cs",
    (
        'ScenePath = "Assets/Scenes/PhraseLayerReadMvp.unity"',
        "root.AddComponent<MetaPassthroughCameraBridge>()",
        "root.AddComponent<OcrViewportDebugBehaviour>()",
        "root.AddComponent<OcrDebugRuntimeBehaviour>()",
        "root.AddComponent<UnityPaddleOcrBootstrapBehaviour>()",
        "root.AddComponent<UnityLearnerProfileBehaviour>()",
        "root.AddComponent<QuestReadAssistanceDebugBehaviour>()",
        'AssignReference(runtimeDriver, "cameraBridge", cameraBridge)',
        'AssignReference(runtimeDriver, "presenter", presenter)',
        'AssignReference(ocrBootstrap, "runtimeDriver", runtimeDriver)',
        'AssignReference(readAssistance, "ocrPresenter", presenter)',
        'AssignReference(readAssistance, "learnerProfile", learnerProfile)',
        "EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) }",
        "PhraseLayerLocalOcrAssets.AssignLocalAssetsToSceneBootstrap()",
        "TryWireLocalTranslation(root, readAssistance)",
        "root.AddComponent<UnityLocalTranslationAssetGateBehaviour>()",
        "root.AddComponent<UnityLocalTranslationBootstrapBehaviour>()",
        'AssignReference(bootstrap, "readAssistance", readAssistance)',
        'AssignReference(bootstrap, "encoderModel", encoder)',
        'AssignReference(bootstrap, "decoderModel", decoder)',
        "ParityVerifiedTranslationTokenizer.Verify(tokenizer, fixtureSet)",
        "UnityOpusMtModelProbe.ValidateAndBuildReport(encoder, decoder)",
    ),
    "Local Read MVP deterministic scene wiring",
)

require(
    SCRIPTS / "UnityLocalTranslationBootstrapBehaviour.cs",
    (
        "ValidateBootstrapAssets(",
        "ManagedSentencePieceManifest.ParseTokenizer",
        "TranslationTokenizerFixtureManifest.Parse",
        "UnityOpusMtModelProbe.ValidateAndBuildReport",
        "new UnityOpusMtAutoregressiveBackend(",
        "OpusMtEnJapLocalEngineFactory.CreateReferenceEngine(",
        "readAssistance.ConfigureTranslationEngine(engine)",
        "candidateBackend?.Dispose()",
    ),
    "Local translation fail-closed Read bootstrap",
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

require(
    TESTS / "ViewportEnvelopeStabilizerTests.cs",
    (
        "FirstObservationIsAcceptedWithoutLag",
        "SmallOcrJitterIsExponentiallySmoothed",
        "LargeViewportMotionResetsImmediately",
        "TargetsHaveIndependentStateAndResetClearsEncounterGeometry",
        "InvalidOptionsFailClosed",
    ),
    "Viewport overlay stabilization regression tests",
)

if violations:
    raise SystemExit("\n".join(violations))

print(
    "PASS: OCR observation/frame pairs feed one downstream semantic/spatial Read pipeline; language plans are frozen "
    "per encounter; small OCR viewport jitter is stabilized without delaying large motion; the committed camera starts "
    "at XR origin and receives Unity XR head pose; the deterministic local Read scene wires camera/OCR/learner/assistance; "
    "local OPUS-MT is injected only after staged asset/tokenizer/model validation; and stale/noisy frames cannot flicker "
    "the overlay"
)
