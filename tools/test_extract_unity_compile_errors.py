#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "tools" / "extract_unity_compile_errors.py"

spec = importlib.util.spec_from_file_location("extract_unity_compile_errors", MODULE_PATH)
if spec is None or spec.loader is None:
    raise SystemExit("failed to load extract_unity_compile_errors.py")
module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = module
spec.loader.exec_module(module)


def assert_equal(actual, expected, label: str) -> None:
    if actual != expected:
        raise AssertionError(f"{label}: expected {expected!r}, got {actual!r}")


def test_unity_file_diagnostic() -> None:
    lines = [
        "some setup",
        "Assets/Scripts/Foo.cs(12,34): error CS0246: The type or namespace name 'Missing' could not be found",
        "Unity Player Export Failure",
    ]
    diagnostics = module.extract_compiler_diagnostics(lines)
    assert_equal(len(diagnostics), 1, "diagnostic count")
    diagnostic = diagnostics[0]
    assert_equal(diagnostic.code, "CS0246", "code")
    assert_equal(diagnostic.path, "Assets/Scripts/Foo.cs", "path")
    assert_equal(diagnostic.line, 12, "source line")
    assert_equal(diagnostic.column, 34, "source column")
    assert_equal(diagnostic.log_line, 2, "log line")


def test_package_cache_diagnostic_and_duplicate_collapse() -> None:
    line = (
        "Library/PackageCache/com.example@1.2.3/Runtime/Bar.cs(8,2): "
        "error CS1061: 'Thing' does not contain a definition for 'Value'"
    )
    diagnostics = module.extract_compiler_diagnostics([line, line])
    assert_equal(len(diagnostics), 1, "deduplicated diagnostic count")
    assert_equal(diagnostics[0].code, "CS1061", "package diagnostic code")
    assert_equal(
        diagnostics[0].path,
        "Library/PackageCache/com.example@1.2.3/Runtime/Bar.cs",
        "package diagnostic path",
    )


def test_global_compiler_diagnostic_without_file() -> None:
    diagnostics = module.extract_compiler_diagnostics(
        ["error CS2001: Source file 'Generated.cs' could not be found."]
    )
    assert_equal(len(diagnostics), 1, "global diagnostic count")
    assert_equal(diagnostics[0].path, None, "global diagnostic path")
    assert_equal(diagnostics[0].code, "CS2001", "global diagnostic code")


def test_warning_is_not_promoted_to_error() -> None:
    diagnostics = module.extract_compiler_diagnostics(
        ["Assets/Foo.cs(1,1): warning CS0168: The variable 'x' is declared but never used"]
    )
    assert_equal(diagnostics, [], "warnings excluded")


def test_generic_summaries_are_not_concrete_causes() -> None:
    lines = [
        "Script Compiler Error",
        "Your Unity scripts failed to compile. Check the logs for specific error messages.",
        "Unity Player Export Failure",
        "Your Unity build failed during the export process.",
    ]
    assert_equal(module.extract_compiler_diagnostics(lines), [], "generic summaries excluded")
    summaries = module.find_generic_summary_lines(lines)
    assert_equal(len(summaries), 4, "generic summary count")


def test_cli_file_loading_preserves_invalid_bytes_as_replacement() -> None:
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "build.log"
        path.write_bytes(
            b"prefix\xff\nAssets/Test.cs(4,5): error CS0103: The name 'x' does not exist\n"
        )
        lines = module.load_lines(str(path))
        diagnostics = module.extract_compiler_diagnostics(lines)
        assert_equal(len(diagnostics), 1, "replacement-decoded diagnostic count")
        assert_equal(diagnostics[0].line, 4, "replacement-decoded source line")


def test_missing_log_is_treated_as_empty() -> None:
    with tempfile.TemporaryDirectory() as directory:
        missing = Path(directory) / "not-created.log"
        assert_equal(module.load_lines(str(missing)), [], "missing preflight log")


def main() -> int:
    test_unity_file_diagnostic()
    test_package_cache_diagnostic_and_duplicate_collapse()
    test_global_compiler_diagnostic_without_file()
    test_warning_is_not_promoted_to_error()
    test_generic_summaries_are_not_concrete_causes()
    test_cli_file_loading_preserves_invalid_bytes_as_replacement()
    test_missing_log_is_treated_as_empty()
    print("PASS: Unity compiler log extractor isolates concrete CS errors and tolerates skipped compile logs")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
