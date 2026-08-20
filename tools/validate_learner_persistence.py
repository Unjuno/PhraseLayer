#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "PhraseLayer.Core"
UNITY = ROOT / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts"
violations = []


def require_file(path: Path) -> str:
    if not path.is_file():
        violations.append(f"missing file: {path.relative_to(ROOT)}")
        return ""
    return path.read_text(encoding="utf-8")


def require_markers(text: str, label: str, markers: tuple[str, ...]) -> None:
    for marker in markers:
        if marker not in text:
            violations.append(f"{label} missing reviewed marker: {marker}")


core = require_file(CORE / "LearnerProfilePersistence.cs")
learning = require_file(CORE / "Learning.cs")
adaptation = require_file(CORE / "LearnerAdaptation.cs")
encounter = require_file(CORE / "LearningEncounterSession.cs")
store = require_file(UNITY / "UnityLearnerProfileStore.cs")
service = require_file(UNITY / "UnityLearnerProfileBehaviour.cs")
demo = require_file(UNITY / "PhraseLayerDemoBehaviour.cs")
tests = require_file(ROOT / "tests" / "PhraseLayer.Core.Tests" / "LearnerProfilePersistenceTests.cs")
adaptation_tests = require_file(ROOT / "tests" / "PhraseLayer.Core.Tests" / "LearnerAdaptationTests.cs")
encounter_tests = require_file(ROOT / "tests" / "PhraseLayer.Core.Tests" / "LearningEncounterSessionTests.cs")

require_markers(
    core,
    "learner persistence Core",
    (
        "CurrentSchemaVersion = 1",
        "interface IMutableLearnerModel : ILearnerModel",
        "interface ILearnerProfileStore",
        "sealed class PersistentLearnerModel",
        "store.Save(inner.CreateSnapshot())",
        "duplicate normalized key",
    ),
)
require_markers(
    learning,
    "in-memory learner model",
    (
        "InMemoryLearnerModel : IMutableLearnerModel",
        "LearnerProfileSnapshot CreateSnapshot()",
        "void LoadSnapshot(LearnerProfileSnapshot snapshot)",
        "FromSnapshot(LearnerProfileSnapshot snapshot)",
    ),
)
require_markers(
    adaptation,
    "learner observation updater",
    (
        "VerifiedUnaidedSuccess = 7",
        "enum LearningObservationOrigin",
        "sealed class LearningObservation",
        "case LearningEvidenceKind.AssistedExposure:",
        "case LearningEvidenceKind.CompletedWithoutAssistance:",
        "applied: false",
        "Do not even write the unchanged value",
        "EnsureEngagement(observation)",
        "LearningObservationOrigin.RecallProbe",
        "AssistanceRequested is action-dependent",
    ),
)
require_markers(
    encounter,
    "learning encounter evidence boundary",
    (
        "does NOT synthesize learning evidence",
        "RecordVerifiedUnaidedSuccess",
        "Cannot record verified unaided success",
        "successfulUnassistedCompletion is retained as encounter metadata",
        "if (update.Applied) updates.Add(update)",
        "ValidateObservationAgainstEncounter(observation)",
        "Assistance request origin does not match the display action",
    ),
)
require_markers(
    store,
    "Unity learner profile store",
    (
        "Application.persistentDataPath",
        "learner-profile-v1.json",
        "JsonUtility.ToJson",
        "JsonUtility.FromJson<ProfileDto>",
        "File.Move(FilePath, BackupPath)",
        "File.Move(TemporaryPath, FilePath)",
        "ILearnerProfileStore",
    ),
)
require_markers(
    service,
    "Unity persistent learner service",
    (
        "new UnityLearnerProfileStore()",
        "new PersistentLearnerModel(store, fallbackDefaultUnderstanding)",
        "IMutableLearnerModel Model",
        "SetUnderstanding(string text, double understanding)",
    ),
)
require_markers(
    tests,
    "learner persistence tests",
    (
        "SnapshotRejectsDuplicateNormalizedKeys",
        "InMemoryModelRoundTripsSnapshotAndReplacesOldState",
        "PersistentModelLoadsExistingProfileWithoutWritingItBack",
        "PersistentMutationSavesOneNormalizedSnapshot",
    ),
)
require_markers(
    adaptation_tests,
    "learner adaptation tests",
    (
        "AssistedExposureDoesNotMutateOrCreateExplicitKnowledge",
        "SilentCompletionDoesNotBecomeMasteryEvidence",
        "VerifiedUnaidedSuccessRaisesUnderstandingAndReducesAutoSupport",
        "PersistentLearnerDoesNotSaveNoEvidenceButSavesRecall",
        "IncompatibleObservationOriginIsRejected",
        "AssistanceRequestWithoutDisplayActionIsRejected",
    ),
)
require_markers(
    encounter_tests,
    "learning encounter tests",
    (
        "FinishWithoutExplicitEvidenceDoesNotMutateLearnerState",
        "SuccessfulCompletionFlagDoesNotInventEvidence",
        "VerifiedUnaidedSuccessMustBeSpecificAndUnassisted",
        "AssistanceRequestRecordsActionThatGeneratedObservation",
        "ExplicitObservationOriginCannotContradictEncounterDisplayAction",
        "ConvenienceRecordStillConditionsOnActualDisplayAction",
        "GenericVerifiedUnaidedEvidenceCannotBypassInterventionCensoring",
    ),
)

# Demo controls are synthetic developer tooling; keep them away from the persisted production profile.
if "private InMemoryLearnerModel learner;" not in demo:
    violations.append("PhraseLayerDemoBehaviour must remain explicitly ephemeral and use InMemoryLearnerModel")
if "UnityLearnerProfileStore" in demo or "PersistentLearnerModel" in demo:
    violations.append("PhraseLayerDemoBehaviour must not write production learner persistence")
if "Continue (no evidence)" not in demo or "No mastery was inferred from passive continuation or silence." not in demo:
    violations.append("PhraseLayerDemoBehaviour must expose the no-evidence encounter behavior explicitly")

# Persistence-format and filesystem API calls must stay outside Core. Comments may discuss the
# platform boundary, so validate concrete API/type references instead of English substrings.
for forbidden in ("using UnityEngine", "UnityEngine.", "JsonUtility.", "Application.persistentDataPath", "using System.IO"):
    if forbidden in core:
        violations.append(f"LearnerProfilePersistence.cs must remain platform-neutral; found {forbidden}")

if violations:
    raise SystemExit("\n".join(violations))

print(
    "PASS: learner persistence remains local/platform-neutral; passive/silent events do not mutate state, "
    "and applied observations are explicit/action-aware"
)
