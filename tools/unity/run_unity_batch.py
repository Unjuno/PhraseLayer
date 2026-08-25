#!/usr/bin/env python3
from __future__ import annotations

import argparse
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
from typing import Sequence

TOOLS_DIR = Path(__file__).resolve().parents[1]
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

from extract_unity_compile_errors import (  # noqa: E402
    CompilerDiagnostic,
    extract_compiler_diagnostics,
    find_generic_summary_lines,
)

EXIT_CONFIGURATION = 2
EXIT_COMPILE_ERROR = 10
EXIT_UNITY_FAILURE = 11
EXIT_LOG_MISSING = 12
EXIT_TIMEOUT = 124


def resolve_editor(value: str) -> str:
    candidate = Path(value).expanduser()
    if candidate.is_file():
        return str(candidate.resolve())

    resolved = shutil.which(value)
    if resolved:
        return resolved

    raise FileNotFoundError(f"Unity editor executable not found: {value}")


def terminate_process_tree(process: subprocess.Popen[object]) -> None:
    if process.poll() is not None:
        return

    try:
        if os.name == "nt":
            process.terminate()
        else:
            os.killpg(process.pid, signal.SIGTERM)
    except (ProcessLookupError, OSError):
        pass

    try:
        process.wait(timeout=10)
        return
    except subprocess.TimeoutExpired:
        pass

    try:
        if os.name == "nt":
            process.kill()
        else:
            os.killpg(process.pid, signal.SIGKILL)
    except (ProcessLookupError, OSError):
        pass


def classify_result(
    return_code: int | None,
    timed_out: bool,
    lines: Sequence[str],
) -> tuple[str, int, list[CompilerDiagnostic]]:
    diagnostics = extract_compiler_diagnostics(lines)
    if diagnostics:
        return "compile-error", EXIT_COMPILE_ERROR, diagnostics
    if timed_out:
        return "timeout", EXIT_TIMEOUT, diagnostics
    if return_code != 0:
        return "unity-failure", EXIT_UNITY_FAILURE, diagnostics
    return "pass", 0, diagnostics


def print_log_tail(lines: Sequence[str], count: int) -> None:
    if not lines:
        return
    print(f"--- Unity log tail ({min(count, len(lines))} lines) ---")
    for line in lines[-count:]:
        print(line)


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Run PhraseLayer's real Unity batch verification with a hard timeout and classify failures "
            "into compiler errors, Unity failures, missing logs, or timeouts."
        )
    )
    parser.add_argument("--unity-editor", required=True)
    parser.add_argument("--project", required=True)
    parser.add_argument(
        "--execute-method",
        default="PhraseLayer.Unity.Editor.PhraseLayerEditorVerification.VerifyCorePipelineBatch",
    )
    parser.add_argument("--log-file", required=True)
    parser.add_argument("--timeout-seconds", type=int, default=900)
    parser.add_argument("--tail-lines", type=int, default=80)
    args = parser.parse_args()

    if args.timeout_seconds <= 0:
        print("ERROR: --timeout-seconds must be positive", file=sys.stderr)
        return EXIT_CONFIGURATION

    project = Path(args.project).expanduser().resolve()
    if not project.is_dir():
        print(f"ERROR: Unity project directory not found: {project}", file=sys.stderr)
        return EXIT_CONFIGURATION

    log_path = Path(args.log_file).expanduser().resolve()
    log_path.parent.mkdir(parents=True, exist_ok=True)
    if log_path.exists():
        log_path.unlink()

    try:
        editor = resolve_editor(args.unity_editor)
    except FileNotFoundError as exception:
        print(f"ERROR: {exception}", file=sys.stderr)
        return EXIT_CONFIGURATION

    command = [
        editor,
        "-batchmode",
        "-nographics",
        "-projectPath",
        str(project),
        "-executeMethod",
        args.execute_method,
        "-logFile",
        str(log_path),
    ]

    print("PhraseLayer real Unity verification starting")
    print(f"project={project}")
    print(f"method={args.execute_method}")
    print(f"timeout_seconds={args.timeout_seconds}")
    print(f"log={log_path}")

    popen_kwargs: dict[str, object] = {
        "cwd": str(project),
        "stdin": subprocess.DEVNULL,
        "stdout": subprocess.DEVNULL,
        "stderr": subprocess.DEVNULL,
    }
    if os.name == "nt":
        popen_kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        popen_kwargs["start_new_session"] = True

    timed_out = False
    process: subprocess.Popen[object] | None = None
    try:
        process = subprocess.Popen(command, **popen_kwargs)
        try:
            return_code = process.wait(timeout=args.timeout_seconds)
        except subprocess.TimeoutExpired:
            timed_out = True
            terminate_process_tree(process)
            return_code = process.poll()
    except OSError as exception:
        print(f"ERROR: failed to launch Unity: {exception}", file=sys.stderr)
        return EXIT_CONFIGURATION

    if not log_path.is_file():
        result = "timeout" if timed_out else "log-missing"
        print(f"PHRASELAYER_UNITY_RESULT={result}")
        if timed_out:
            print("ERROR: Unity exceeded the hard timeout and produced no log.", file=sys.stderr)
            return EXIT_TIMEOUT
        print("ERROR: Unity exited without creating the requested log file.", file=sys.stderr)
        return EXIT_LOG_MISSING

    lines = log_path.read_text(encoding="utf-8", errors="replace").splitlines()
    outcome, exit_code, diagnostics = classify_result(return_code, timed_out, lines)
    print(f"PHRASELAYER_UNITY_RESULT={outcome}")
    print(f"unity_exit={return_code}")

    if diagnostics:
        print(f"Concrete Unity/Roslyn compiler errors: {len(diagnostics)}")
        for diagnostic in diagnostics:
            print(diagnostic.render())
        if timed_out:
            print("NOTE: Unity also exceeded the hard timeout; compiler diagnostics remain the primary actionable cause.")
        return exit_code

    if outcome == "timeout":
        print(f"ERROR: Unity exceeded {args.timeout_seconds} seconds; process tree was terminated.", file=sys.stderr)
        print_log_tail(lines, args.tail_lines)
        return exit_code

    if outcome == "unity-failure":
        generic = find_generic_summary_lines(lines)
        if generic:
            print(f"Generic Unity failure summaries: {len(generic)}")
            for log_line, text in generic[:5]:
                print(f"log line {log_line}: {text}")
        print_log_tail(lines, args.tail_lines)
        return exit_code

    print("PhraseLayer real Unity verification PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
