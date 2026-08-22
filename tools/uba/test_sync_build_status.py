#!/usr/bin/env python3
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from sync_build_status import (  # noqa: E402
    _project_matches,
    _project_org_id,
    _target_branch,
    _target_id,
    _target_name,
    build_number,
    extract_diagnostics,
    find_matching_build,
    normalized_status,
    revision_matches,
)


class SyncBuildStatusTests(unittest.TestCase):
    def test_matches_full_or_abbreviated_revision(self) -> None:
        sha = "a" * 40
        self.assertTrue(revision_matches({"lastBuiltRevision": sha}, sha))
        self.assertTrue(revision_matches({"changeset": [{"commit": sha[:12]}]}, sha))
        self.assertFalse(revision_matches({"lastBuiltRevision": "b" * 40}, sha))

    def test_selects_latest_matching_build_and_branch(self) -> None:
        sha = "a" * 40
        builds = [
            {"build": 3, "scmBranch": "other", "lastBuiltRevision": sha},
            {"build": 5, "scmBranch": "agent/multi-sentence-segmentation", "lastBuiltRevision": sha},
            {"build": 4, "scmBranch": "agent/multi-sentence-segmentation", "lastBuiltRevision": sha},
        ]
        selected = find_matching_build(builds, sha, "agent/multi-sentence-segmentation")
        self.assertIsNotNone(selected)
        self.assertEqual(build_number(selected), 5)

    def test_extracts_cs_error_before_generic_failure(self) -> None:
        log = "before\nAssets/Test.cs(4,7): error CS0246: MissingThing could not be found\nafter"
        failures = [{"title": "Script Compiler Error", "message": "Your Unity scripts failed to compile."}]
        diagnostics = extract_diagnostics(log, failures)
        self.assertIn("error CS0246", diagnostics[0])
        self.assertNotIn("Your Unity scripts failed", diagnostics[0])

    def test_falls_back_to_categorized_failure(self) -> None:
        diagnostics = extract_diagnostics("", [{"message": "Package resolution failed: dependency missing"}])
        self.assertEqual(diagnostics, ["Package resolution failed: dependency missing"])

    def test_normalizes_known_build_status(self) -> None:
        self.assertEqual(normalized_status("sentToBuilder"), "senttobuilder")
        self.assertEqual(normalized_status("CANCELED"), "canceled")

    def test_project_and_target_discovery_helpers(self) -> None:
        project = {"guid": "project-guid", "orgFk": 12345}
        self.assertTrue(_project_matches(project, "project-guid"))
        self.assertEqual(_project_org_id(project), "12345")
        target = {"buildtargetid": "phrase-layer-quest-ci", "name": "PhraseLayer Quest CI", "settings": {"scm": {"branch": "agent/multi-sentence-segmentation"}}}
        self.assertEqual(_target_id(target), "phrase-layer-quest-ci")
        self.assertEqual(_target_name(target), "PhraseLayer Quest CI")
        self.assertEqual(_target_branch(target), "agent/multi-sentence-segmentation")


if __name__ == "__main__":
    unittest.main()
