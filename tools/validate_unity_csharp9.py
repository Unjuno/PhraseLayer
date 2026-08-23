#!/usr/bin/env python3
"""Fail fast when Unity-compiled PhraseLayer code drifts beyond the Unity 6000/C# 9 assembly baseline."""

from pathlib import Path
import json
import re

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "PhraseLayer.Core"
UNITY = ROOT / "unity" / "PhraseLayer.Unity"
PREFLIGHT = ROOT / "tests" / "PhraseLayer.UnityShell.Compile"
RUNTIME_ASMDEF = UNITY / "Assets" / "PhraseLayer.Unity.asmdef"
EDITOR_ASMDEF = UNITY / "Assets" / "Editor" / "PhraseLayer.Unity.Editor.asmdef"

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

# The runtime assembly must exist inside the Editor because the Editor verification assembly references it.
# A previous diagnostic experiment accidentally added !UNITY_EDITOR to both assemblies; host csproj preflight
# still passed because it compiles source files directly, while real Unity could not satisfy the asmdef graph.
runtime_asmdef = json.loads(RUNTIME_ASMDEF.read_text(encoding="utf-8"))
editor_asmdef = json.loads(EDITOR_ASMDEF.read_text(encoding="utf-8"))
require(
    "!UNITY_EDITOR" not in runtime_asmdef.get("defineConstraints", []),
    "PhraseLayer.Unity.asmdef must compile in the Unity Editor because PhraseLayer.Unity.Editor references it",
)
require(
    "!UNITY_EDITOR" not in editor_asmdef.get("defineConstraints", []),
    "PhraseLayer.Unity.Editor.asmdef cannot require !UNITY_EDITOR while includePlatforms contains Editor",
)
require(
    "Editor" in editor_asmdef.get("includePlatforms", []),
    "PhraseLayer.Unity.Editor.asmdef must remain explicitly Editor-only",
)
require(
    "PhraseLayer.Unity" in editor_asmdef.get("references", []),
    "PhraseLayer.Unity.Editor.asmdef must retain its runtime assembly reference",
)

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

print("PASS: Unity/Core compilation surface remains within C# 9 and the Editor/runtime asmdef graph is satisfiable")
