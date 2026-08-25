#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
from pathlib import Path
import sys

MODULE_PATH = Path(__file__).with_name("run_unity_batch.py")
spec = importlib.util.spec_from_file_location("run_unity_batch", MODULE_PATH)
if spec is None or spec.loader is None:
    raise SystemExit("failed to load run_unity_batch.py")
module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = module
spec.loader.exec_module(module)


def assert_equal(actual, expected, label: str) -> None:
    if actual != expected:
        raise AssertionError(f"{label}: expected {expected!r}, got {actual!r}")


def main() -> int:
    outcome, code, diagnostics = module.classify_result(0, False, ["Unity batch complete"])
    assert_equal(outcome, "pass", "success outcome")
    assert_equal(code, 0, "success exit")
    assert_equal(len(diagnostics), 0, "success diagnostics")

    compile_log = [
        "Assets/Scripts/Broken.cs(12,7): error CS1002: ; expected",
        "Scripts have compiler errors.",
    ]
    outcome, code, diagnostics = module.classify_result(1, False, compile_log)
    assert_equal(outcome, "compile-error", "compile outcome")
    assert_equal(code, module.EXIT_COMPILE_ERROR, "compile exit")
    assert_equal(len(diagnostics), 1, "compile diagnostics")
    assert_equal(diagnostics[0].code, "CS1002", "compiler code")
    assert_equal(diagnostics[0].path, "Assets/Scripts/Broken.cs", "compiler path")
    assert_equal(diagnostics[0].line, 12, "compiler line")

    outcome, code, diagnostics = module.classify_result(1, True, compile_log)
    assert_equal(outcome, "compile-error", "compiler diagnostics outrank timeout")
    assert_equal(code, module.EXIT_COMPILE_ERROR, "compiler timeout exit")
    assert_equal(len(diagnostics), 1, "compiler timeout diagnostics")

    outcome, code, diagnostics = module.classify_result(None, True, ["Resolving packages..."])
    assert_equal(outcome, "timeout", "timeout outcome")
    assert_equal(code, module.EXIT_TIMEOUT, "timeout exit")
    assert_equal(len(diagnostics), 0, "timeout diagnostics")

    outcome, code, diagnostics = module.classify_result(1, False, ["Build failed during the export process"])
    assert_equal(outcome, "unity-failure", "generic failure outcome")
    assert_equal(code, module.EXIT_UNITY_FAILURE, "generic failure exit")
    assert_equal(len(diagnostics), 0, "generic failure diagnostics")

    print("PASS: real Unity batch runner classifies compiler errors, timeouts, and Unity failures deterministically")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
