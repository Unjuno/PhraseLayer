#!/usr/bin/env python3
"""Fail fast when Unity-compiled PhraseLayer code drifts beyond the Unity 6000 C# 9 baseline."""

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "PhraseLayer.Core"
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
PREFLIGHT = ROOT / "tests" / "PhraseLayer.UnityShell.Compile"

violations: list[str] = []


def require(condition: bool, message: str) -> None:
    if not condition:
        violations.append(message)


# Keep every host-side compiler that stands in for Unity pinned to the same language generation.
for path in (
    CORE / "Directory.Build.props",
    PREFLIGHT / "PhraseLayer.UnityShell.Compile.csproj",
    PREFLIGHT / "PhraseLayer.UnityAndroid.Compile.csproj",
):
    text = path.read_text(encoding="utf-8")
    require("<LangVersion>9.0</LangVersion>" in text, f"{path.relative_to(ROOT)} must pin C# 9.0")

# These constructs are unambiguously newer than C# 9 and should never reach Unity compilation.
patterns = (
    (re.compile(r"^\s*global\s+using\s+", re.MULTILINE), "global using (C# 10+)"),
    (re.compile(r"^\s*namespace\s+[A-Za-z_][\w.]*\s*;", re.MULTILINE), "file-scoped namespace (C# 10+)"),
    (re.compile(r"\brecord\s+struct\b"), "record struct (C# 10+)"),
    (re.compile(r'"""'), "raw string literal (C# 11+)"),
)

for root in (CORE, UNITY / "Assets" / "Scripts", UNITY / "Assets" / "Editor"):
    for path in root.rglob("*.cs"):
        text = path.read_text(encoding="utf-8")
        for pattern, label in patterns:
            if pattern.search(text):
                violations.append(f"{path.relative_to(ROOT)} uses {label}")

if violations:
    raise SystemExit("\n".join(violations))

print("PASS: Unity/Core compilation surface remains within the pinned C# 9 language baseline")
