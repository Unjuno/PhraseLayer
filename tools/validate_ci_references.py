#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"

PYTHON_TOOL = re.compile(r"\bpython(?:3(?:\.\d+)?)?\s+(?P<path>tools/[A-Za-z0-9_./-]+\.py)\b")
DOTNET_PROJECT = re.compile(
    r"\bdotnet\s+(?:restore|build|test|run)\s+(?P<path>[A-Za-z0-9_./-]+\.csproj)\b"
)
LOCAL_USE = re.compile(r"^\s*-?\s*uses:\s*[\"']?(?P<path>\./[^\"'\s]+)", re.MULTILINE)
SHELL_TOOL = re.compile(r"(?<![A-Za-z0-9_])(?P<path>\./tools/[A-Za-z0-9_./-]+)(?=\s|$)")


def referenced_paths(text: str) -> list[str]:
    found: list[str] = []
    for pattern in (PYTHON_TOOL, DOTNET_PROJECT, LOCAL_USE, SHELL_TOOL):
        for match in pattern.finditer(text):
            path = match.group("path")
            if path.startswith("./"):
                path = path[2:]
            if path not in found:
                found.append(path)
    return found


def main() -> int:
    if not WORKFLOWS.is_dir():
        print("ERROR: .github/workflows is missing")
        return 1

    errors: list[str] = []
    checked = 0
    workflows = sorted((*WORKFLOWS.glob("*.yml"), *WORKFLOWS.glob("*.yaml")))
    if not workflows:
        errors.append("no GitHub Actions workflows found")

    for workflow in workflows:
        text = workflow.read_text(encoding="utf-8")
        for relative in referenced_paths(text):
            checked += 1
            target = (ROOT / relative).resolve()
            try:
                target.relative_to(ROOT.resolve())
            except ValueError:
                errors.append(f"{workflow.relative_to(ROOT)} references path outside repository: {relative}")
                continue
            if not target.exists():
                errors.append(f"{workflow.relative_to(ROOT)} references missing path: {relative}")

    if errors:
        for error in errors:
            print(f"ERROR: {error}")
        return 1

    print(f"PASS: GitHub Actions repository-local references resolve ({checked} reference(s) checked)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
