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

# Demo controls are synthetic developer tooling; keep them away from the persisted production profile.
if "private InMemoryLearnerModel learner;" not in demo:
    violations.append("PhraseLayerDemoBehaviour must remain explicitly ephemeral and use InMemoryLearnerModel")
if "UnityLearnerProfileStore" in demo or "PersistentLearnerModel" in demo:
    violations.append("PhraseLayerDemoBehaviour must not write production learner persistence")

# Persistence-format and filesystem concerns must stay outside Core.
for forbidden in ("UnityEngine", "JsonUtility", "persistentDataPath", "System.IO"):
    if forbidden in core:
        violations.append(f"LearnerProfilePersistence.cs must remain platform-neutral; found {forbidden}")

if violations:
    raise SystemExit("\n".join(violations))

print("PASS: learner snapshot, persistent model, Unity file store, production service, and ephemeral demo boundaries validated")
