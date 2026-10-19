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
store = require_file(UNITY / "UnityLearnerProfileStore.cs")
service = require_file(UNITY / "UnityLearnerProfileBehaviour.cs")
demo = require_file(UNITY / "PhraseLayerDemoBehaviour.cs")
tests = require_file(ROOT / "tests" / "PhraseLayer.Core.Tests" / "LearnerProfilePersistenceTests.cs")
batch = require_file(CORE / "LearnerUpdateBatch.cs")
session = require_file(CORE / "LearningEncounterSession.cs")
commit_tests = require_file(ROOT / "tests" / "PhraseLayer.Core.Tests" / "LearnerCommitSafetyTests.cs")

require_markers(core, "learner persistence Core", (
    "CurrentSchemaVersion = 1", "interface IMutableLearnerModel : ILearnerModel", "interface ILearnerProfileStore",
    "sealed class PersistentLearnerModel", "PersistThenPublish", "store.Save(snapshot);", "inner.LoadSnapshot(snapshot);",
    "Array.AsReadOnly", "duplicate normalized key"))
# A structural guard, not proof of transactional runtime behavior; executable fault-injection tests are required.
if "store.Save(snapshot);" in core and "inner.LoadSnapshot(snapshot);" in core:
    if core.index("store.Save(snapshot);") > core.index("inner.LoadSnapshot(snapshot);"):
        violations.append("persistent learner must save before publishing in-memory state")
require_markers(batch, "frozen batch", ("BeforeSnapshot", "AfterSnapshot", "learner.LoadSnapshot(AfterSnapshot)", "state changed after batch preparation"))
require_markers(session, "encounter commit", ("adaptation.PrepareBatch", "preparedBatch.Commit()", "IsCommitPending"))
require_markers(commit_tests, "fault injection tests", ("FailedSaveRetryUsesOneFrozenSnapshotWithoutDoubleLearning", "InterveningEvidenceCannotBeOverwrittenByAnOldPreparedBatch"))
require_markers(learning, "in-memory learner model", ("InMemoryLearnerModel : IMutableLearnerModel", "LearnerProfileSnapshot CreateSnapshot()",
    "void LoadSnapshot(LearnerProfileSnapshot snapshot)", "FromSnapshot(LearnerProfileSnapshot snapshot)"))
require_markers(store, "Unity learner profile store", ("Application.persistentDataPath", "learner-profile-v1.json", "JsonUtility.ToJson",
    "JsonUtility.FromJson<ProfileDto>", "File.Move(FilePath, BackupPath)", "File.Move(TemporaryPath, FilePath)", "ILearnerProfileStore"))
require_markers(service, "Unity persistent learner service", ("new UnityLearnerProfileStore()", "new PersistentLearnerModel(store, fallbackDefaultUnderstanding)",
    "IMutableLearnerModel Model", "SetUnderstanding(string text, double understanding)"))
require_markers(tests, "learner persistence tests", ("SnapshotRejectsDuplicateNormalizedKeys", "InMemoryModelRoundTripsSnapshotAndReplacesOldState",
    "PersistentModelLoadsExistingProfileWithoutWritingItBack", "PersistentMutationSavesOneNormalizedSnapshot"))
if "private InMemoryLearnerModel learner;" not in demo:
    violations.append("PhraseLayerDemoBehaviour must remain explicitly ephemeral and use InMemoryLearnerModel")
if "UnityLearnerProfileStore" in demo or "PersistentLearnerModel" in demo:
    violations.append("PhraseLayerDemoBehaviour must not write production learner persistence")
for forbidden in ("using UnityEngine", "UnityEngine.", "JsonUtility.", "Application.persistentDataPath", "using System.IO"):
    if forbidden in core:
        violations.append(f"LearnerProfilePersistence.cs must remain platform-neutral; found {forbidden}")
if violations:
    raise SystemExit("\n".join(violations))
print("PASS: persistence boundaries, save-before-publish wiring and frozen retry batch markers; runtime fault-injection tests are separate")
