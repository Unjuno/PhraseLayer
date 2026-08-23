#!/usr/bin/env python3
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
CORE_CI = ROOT / ".github" / "workflows" / "core-ci.yml"
UBA_FEEDBACK = ROOT / ".github" / "workflows" / "uba-feedback.yml"


def require(condition: bool, message: str, errors: list[str]) -> None:
    if not condition:
        errors.append(message)


def main() -> int:
    errors: list[str] = []
    if not CORE_CI.is_file():
        errors.append("missing .github/workflows/core-ci.yml")
    if not UBA_FEEDBACK.is_file():
        errors.append("missing .github/workflows/uba-feedback.yml")
    if errors:
        for error in errors:
            print("ERROR: " + error)
        return 1

    core = CORE_CI.read_text(encoding="utf-8")
    uba = UBA_FEEDBACK.read_text(encoding="utf-8")

    require("concurrency:" in core, "Core CI must define concurrency", errors)
    require("cancel-in-progress: true" in core, "Core CI must cancel stale branch runs", errors)
    require("timeout-minutes: 20" in core, "Core CI jobs must have bounded execution", errors)
    require("Publish compiler diagnostic statuses best-effort" in core,
            "compiler status publishing must remain an explicitly best-effort step", errors)
    require("continue-on-error: true\n        timeout-minutes: 1\n        uses: actions/github-script@v7" in core,
            "compiler status publishing must be non-blocking and capped at one minute", errors)
    require("publish-unity-preflight-status:" not in core,
            "duplicate preflight status publisher job must not be reintroduced", errors)
    require("target_url:" not in core,
            "Core CI diagnostic statuses must not depend on run URL publication", errors)
    require("phraselayer/unity-run-${process.env.GITHUB_RUN_ID}" in core,
            "Core CI must expose the run id through a status context for MCP diagnostics", errors)

    uba_trigger = uba.split("permissions:", 1)[0]
    require("workflow_dispatch:" in uba_trigger,
            "UBA feedback must remain manually invokable", errors)
    require("\n  push:" not in uba_trigger,
            "UBA feedback must not poll Unity Build Automation on every push", errors)
    require('UNITY_UBA_POLL_TIMEOUT: "600"' in uba,
            "manual UBA polling must remain bounded to ten minutes", errors)
    require("timeout-minutes: 15" in uba,
            "manual UBA feedback job must have a short hard timeout", errors)
    require("target_url:" not in uba,
            "UBA feedback statuses must not depend on execution URL publication", errors)

    if errors:
        for error in errors:
            print("ERROR: " + error)
        return 1

    print("PASS: CI diagnostics are bounded, stale runs cancel, and UBA polling is manual-only")
    return 0


if __name__ == "__main__":
    sys.exit(main())
