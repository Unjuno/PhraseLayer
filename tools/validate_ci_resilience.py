#!/usr/bin/env python3
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"
CORE_CI = WORKFLOWS / "core-ci.yml"
CORE_STATUS = WORKFLOWS / "core-ci-status.yml"
UBA_FEEDBACK = WORKFLOWS / "uba-feedback.yml"
UNITY_CLI = WORKFLOWS / "unity-cli.yml"


def require(condition: bool, message: str, errors: list[str]) -> None:
    if not condition:
        errors.append(message)


def main() -> int:
    errors: list[str] = []
    if not WORKFLOWS.is_dir():
        errors.append("missing .github/workflows")
    if not CORE_CI.is_file():
        errors.append("missing .github/workflows/core-ci.yml")
    if not CORE_STATUS.is_file():
        errors.append("missing .github/workflows/core-ci-status.yml")
    if not UBA_FEEDBACK.is_file():
        errors.append("missing .github/workflows/uba-feedback.yml")
    if not UNITY_CLI.is_file():
        errors.append("missing .github/workflows/unity-cli.yml")
    if errors:
        for error in errors:
            print("ERROR: " + error)
        return 1

    core = CORE_CI.read_text(encoding="utf-8")
    core_status = CORE_STATUS.read_text(encoding="utf-8")
    uba = UBA_FEEDBACK.read_text(encoding="utf-8")
    unity_cli = UNITY_CLI.read_text(encoding="utf-8")

    require("concurrency:" in core, "Core CI must define concurrency", errors)
    require("cancel-in-progress: true" in core, "Core CI must cancel stale branch runs", errors)
    require("timeout-minutes: 20" in core, "Core CI jobs must have bounded execution", errors)
    require("python tools/unity/test_run_unity_batch.py" in core,
            "Core CI must test the real-Unity timeout/failure classifier on every push", errors)
    require("Publish compiler diagnostic statuses best-effort" in core,
            "compiler status publishing must remain an explicitly best-effort step", errors)
    require("continue-on-error: true\n        timeout-minutes: 1\n        uses: actions/github-script@v7" in core,
            "compiler status publishing must be non-blocking and capped at one minute", errors)
    require("publish-unity-preflight-status:" not in core,
            "duplicate preflight status publisher job must not be reintroduced", errors)
    require("phraselayer/unity-run-${process.env.GITHUB_RUN_ID}" in core,
            "Core CI must expose the run id through a status context for MCP diagnostics", errors)

    require("workflow_run:" in core_status,
            "Core CI status bridge must remain driven by workflow_run completion", errors)
    require("phraselayer/core-ci-run-${run.id}" in core_status,
            "Core CI status bridge must expose the run id in the status context", errors)

    uba_trigger = uba.split("permissions:", 1)[0]
    require("workflow_dispatch:" in uba_trigger,
            "UBA feedback must remain manually invokable", errors)
    require("\n  push:" not in uba_trigger,
            "UBA feedback must not poll Unity Build Automation on every push", errors)
    require('UNITY_UBA_POLL_TIMEOUT: "600"' in uba,
            "manual UBA polling must remain bounded to ten minutes", errors)
    require("timeout-minutes: 15" in uba,
            "manual UBA feedback job must have a short hard timeout", errors)

    unity_trigger = unity_cli.split("permissions:", 1)[0]
    require("workflow_dispatch:" in unity_trigger,
            "real Unity CLI verification must remain explicitly invokable", errors)
    require("\n  push:" not in unity_trigger,
            "real Unity CLI must not queue on every push when the self-hosted Unity runner is offline", errors)
    require("concurrency:" in unity_cli and "cancel-in-progress: true" in unity_cli,
            "real Unity CLI must cancel stale runs", errors)
    require("timeout-minutes: 20" in unity_cli,
            "real Unity CLI job must have a hard GitHub Actions timeout", errors)
    require('PHRASELAYER_UNITY_TIMEOUT_SECONDS: "900"' in unity_cli,
            "real Unity process must have a fifteen-minute subprocess timeout", errors)
    require("./tools/unity/verify.sh" in unity_cli,
            "real Unity CLI must use the fail-fast verification wrapper", errors)
    require("tools/extract_unity_compile_errors.py .ci/unity-real.log" in unity_cli,
            "real Unity CLI must summarize concrete compiler diagnostics", errors)
    require("Upload real Unity log" in unity_cli and "actions/upload-artifact@v4" in unity_cli,
            "real Unity CLI must retain the full Unity log for diagnosis", errors)

    verify_sh = ROOT / "tools" / "unity" / "verify.sh"
    real_runner = ROOT / "tools" / "unity" / "run_unity_batch.py"
    runner_test = ROOT / "tools" / "unity" / "test_run_unity_batch.py"
    require(verify_sh.is_file(), "missing tools/unity/verify.sh", errors)
    require(real_runner.is_file(), "missing tools/unity/run_unity_batch.py", errors)
    require(runner_test.is_file(), "missing tools/unity/test_run_unity_batch.py", errors)
    if verify_sh.is_file():
        verify_text = verify_sh.read_text(encoding="utf-8")
        require("run_unity_batch.py" in verify_text,
                "verify.sh must delegate to the bounded real-Unity runner", errors)
        require("PHRASELAYER_UNITY_TIMEOUT_SECONDS" in verify_text,
                "verify.sh must expose the Unity subprocess timeout", errors)

    # Diagnostic correlation is ID-based, not URL-based. Scan every workflow so a future helper
    # cannot silently reintroduce the execution-URL publication path that previously stalled CI.
    workflow_files = sorted((*WORKFLOWS.glob("*.yml"), *WORKFLOWS.glob("*.yaml")))
    require(bool(workflow_files), "at least one GitHub Actions workflow must exist", errors)
    for workflow in workflow_files:
        text = workflow.read_text(encoding="utf-8")
        relative = workflow.relative_to(ROOT)
        if "target_url:" in text:
            errors.append(f"{relative} must not publish target_url; expose an MCP-readable run id instead")
        if "run.html_url" in text:
            errors.append(f"{relative} must not depend on run.html_url; MCP resolves runs by id")

    if errors:
        for error in errors:
            print("ERROR: " + error)
        return 1

    print(
        "PASS: CI diagnostics are bounded and URL-independent; host compile runs on every push, "
        "real Unity uses a hard subprocess timeout with compiler classification, stale runs cancel, "
        "and offline self-hosted/UBA resources remain manual-only"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
