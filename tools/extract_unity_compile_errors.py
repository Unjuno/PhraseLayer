#!/usr/bin/env python3
from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path
import re
import sys
from typing import Iterable

# Unity/Roslyn commonly emits one of these shapes:
#   Assets/Foo.cs(12,34): error CS0246: ...
#   Packages/com.foo/Bar.cs(8,2): error CS0103: ...
#   error CS2001: Source file '...' could not be found.
CS_DIAGNOSTIC = re.compile(
    r"^(?P<prefix>.*?)(?P<severity>error|warning)\s+(?P<code>CS\d{4})\s*:\s*(?P<message>.+?)\s*$",
    re.IGNORECASE,
)
FILE_LOCATION = re.compile(
    r"(?P<path>(?:[A-Za-z]:)?[^\r\n:]*?\.cs)\((?P<line>\d+),(?P<column>\d+)\)\s*:\s*$",
    re.IGNORECASE,
)
UNITY_FILE_LOCATION = re.compile(
    r"(?P<path>(?:Assets|Packages|Library/PackageCache|Temp|src|unity)/[^\r\n:]*?\.cs)"
    r"\((?P<line>\d+),(?P<column>\d+)\)\s*:\s*$",
    re.IGNORECASE,
)

GENERIC_SUMMARY_MARKERS = (
    "script compiler error",
    "your unity scripts failed to compile",
    "unity player export failure",
    "build failed during the export process",
    "scripts have compiler errors",
)


@dataclass(frozen=True)
class CompilerDiagnostic:
    code: str
    message: str
    path: str | None
    line: int | None
    column: int | None
    log_line: int
    raw: str

    @property
    def key(self) -> tuple[str, str | None, int | None, int | None, str]:
        return (self.code, self.path, self.line, self.column, self.message)

    def render(self) -> str:
        location = ""
        if self.path is not None:
            location = self.path
            if self.line is not None:
                location += f":{self.line}"
                if self.column is not None:
                    location += f":{self.column}"
            location += ": "
        return f"{location}{self.code}: {self.message} [log line {self.log_line}]"


def _normalize_prefix(prefix: str) -> tuple[str | None, int | None, int | None]:
    candidate = prefix.rstrip()
    for pattern in (UNITY_FILE_LOCATION, FILE_LOCATION):
        match = pattern.search(candidate)
        if match:
            path = match.group("path").strip()
            return path, int(match.group("line")), int(match.group("column"))
    return None, None, None


def extract_compiler_diagnostics(lines: Iterable[str]) -> list[CompilerDiagnostic]:
    diagnostics: list[CompilerDiagnostic] = []
    seen: set[tuple[str, str | None, int | None, int | None, str]] = set()

    for log_line, raw_line in enumerate(lines, start=1):
        line = raw_line.rstrip("\r\n")
        match = CS_DIAGNOSTIC.match(line.strip())
        if not match or match.group("severity").lower() != "error":
            continue

        prefix = match.group("prefix")
        path, source_line, column = _normalize_prefix(prefix)
        diagnostic = CompilerDiagnostic(
            code=match.group("code").upper(),
            message=match.group("message").strip(),
            path=path,
            line=source_line,
            column=column,
            log_line=log_line,
            raw=line,
        )
        if diagnostic.key in seen:
            continue
        seen.add(diagnostic.key)
        diagnostics.append(diagnostic)

    return diagnostics


def find_generic_summary_lines(lines: Iterable[str]) -> list[tuple[int, str]]:
    found: list[tuple[int, str]] = []
    for log_line, raw_line in enumerate(lines, start=1):
        normalized = raw_line.strip().lower()
        if any(marker in normalized for marker in GENERIC_SUMMARY_MARKERS):
            found.append((log_line, raw_line.rstrip("\r\n")))
    return found


def load_lines(path: str | None) -> list[str]:
    if path is None or path == "-":
        return sys.stdin.read().splitlines()
    return Path(path).read_text(encoding="utf-8", errors="replace").splitlines()


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Extract primary Roslyn/Unity C# compiler errors from a Unity Build Automation full log."
    )
    parser.add_argument("log", nargs="?", default="-", help="UBA plain-text log path, or - for stdin")
    parser.add_argument(
        "--fail-if-none",
        action="store_true",
        help="return exit code 2 when no concrete error CSxxxx diagnostic is present",
    )
    args = parser.parse_args()

    lines = load_lines(args.log)
    diagnostics = extract_compiler_diagnostics(lines)
    generic = find_generic_summary_lines(lines)

    if diagnostics:
        print(f"Concrete Unity/Roslyn compiler errors: {len(diagnostics)}")
        for diagnostic in diagnostics:
            print(diagnostic.render())
        if generic:
            print(
                f"Generic Unity failure summaries also present: {len(generic)} "
                "(secondary; concrete CS diagnostics above are the actionable cause)."
            )
        return 0

    print("No concrete error CSxxxx diagnostic found.")
    if generic:
        print(
            f"Found {len(generic)} generic Unity failure summary line(s), but they do not identify the compiler cause."
        )
        for log_line, text in generic[:5]:
            print(f"log line {log_line}: {text}")
        print("Use Unity Dashboard -> Build History -> View as plain text / Download log and search for 'error CS'.")

    return 2 if args.fail_if_none else 0


if __name__ == "__main__":
    raise SystemExit(main())
