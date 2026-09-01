#!/usr/bin/env python3
"""Static contract for self-hosted parity/build/device evidence delivery.

The expensive Unity/Android/Quest jobs remain manual and require real hardware, but a successful run must
produce complete evidence and surface the artifact back to the pull request. This validator prevents those
workflow contracts from silently regressing.
"""

from __future__ import annotations

import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
ACTION = ROOT / ".github/actions/notify-pr-artifact/action.yml"
WORKFLOWS = {
    "reference": ROOT / ".github/workflows/moonshine-v1-onnx-reference-probe.yml",
    "marian": ROOT / ".github/workflows/marian-unity-import.yml",
    "moonshine": ROOT / ".github/workflows/moonshine-unity-import.yml",
    "android": ROOT / ".github/workflows/android-il2cpp-listen-mode.yml",
    "quest": ROOT / ".github/workflows/quest3-listen-mode-smoke.yml",
}


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def validate() -> dict[str, object]:
    action = ACTION.read_text(encoding="utf-8")
    for fragment in (
        "actions/github-script@v7",
        "listPullRequestsAssociatedWithCommit",
        "commit_sha: context.sha",
        "github.rest.issues.createComment",
        "PHRASELAYER_ARTIFACT_URL",
    ):
        require(action, fragment, "artifact notifier action")

    common = (
        "issues: write",
        "pull-requests: write",
        "actions/upload-artifact@v4",
        "./.github/actions/notify-pr-artifact",
        "artifact-url: ${{ steps.",
    )
    texts: dict[str, str] = {}
    for name, path in WORKFLOWS.items():
        text = path.read_text(encoding="utf-8")
        texts[name] = text
        for fragment in common:
            require(text, fragment, f"{name} workflow")

    for fragment in (
        "beckett.wav",
        "if-no-files-found: error",
        "moonshine-v1-reference-parity",
    ):
        require(texts["reference"], fragment, "reference workflow")

    for fragment in (
        "Require complete Marian parity evidence",
        "marian.unity-tokens.txt",
        "marian.unity-translation.txt",
        "marian-unity-parity-evidence",
    ):
        require(texts["marian"], fragment, "Marian Unity workflow")

    for fragment in (
        "Require complete Moonshine parity evidence",
        "beckett.wav",
        "beckett.unity-tokens.txt",
        "beckett.unity-transcript.txt",
        "moonshine-unity-parity-evidence",
    ):
        require(texts["moonshine"], fragment, "Moonshine Unity workflow")

    for fragment in (
        "Require complete Android build evidence",
        "PhraseLayer.apk",
        "PhraseLayer.android-build-evidence.json",
        "phraselayer-android-listen-mode",
    ):
        require(texts["android"], fragment, "Android workflow")

    for fragment in (
        "Require complete Quest device evidence",
        "expected_device_model:",
        "quest-listen-mode-smoke.json",
        "quest-startup-logcat.txt",
        "phraselayer-quest3-listen-mode-evidence",
    ):
        require(texts["quest"], fragment, "Quest workflow")

    return {
        "status": "pass",
        "notifier": str(ACTION.relative_to(ROOT)),
        "workflows": sorted(WORKFLOWS),
        "successful_runs_require_complete_evidence": True,
        "successful_runs_publish_pr_artifact_link": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
