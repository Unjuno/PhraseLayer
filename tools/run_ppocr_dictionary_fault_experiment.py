"""Fault-inject generated dictionary artifacts into the already-built production Core replay.

A negative case passes only if the application actually runs and rejects for the expected contract reason.
No source/model changes, network calls, dictionary bytes, decoded text or raw stderr enter the report.
"""
from __future__ import annotations
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def run(args: argparse.Namespace, report: dict) -> None:
    executable = ROOT / "experiments/PhraseLayer.OcrOutputReplay/bin/Release/net8.0/PhraseLayer.OcrOutputReplay.dll"
    if not executable.is_file():
        raise RuntimeError("Build and execute the positive Core replay before fault injection")
    raw = args.dictionary.read_bytes()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    replay = json.loads(args.replay.read_text(encoding="utf-8"))
    tokens = raw.decode("utf-8").removesuffix("\n").split("\n")
    trimmed = b"".join(token.strip().encode("utf-8") + b"\n" for token in tokens)
    if trimmed == raw:
        raise RuntimeError("Whitespace fault was not exercised")
    reordered_tokens = list(tokens)
    different = next(index for index, token in enumerate(tokens) if token != tokens[0])
    reordered_tokens[0], reordered_tokens[different] = reordered_tokens[different], reordered_tokens[0]
    reordered = b"".join(token.encode("utf-8") + b"\n" for token in reordered_tokens)
    forged_trimmed = {**manifest, "generated_sha256": hashlib.sha256(trimmed).hexdigest(), "generated_size_bytes": len(trimmed)}
    forged_reordered = {**manifest, "generated_sha256": hashlib.sha256(reordered).hexdigest(), "generated_size_bytes": len(reordered)}
    reordered_replay = copy.deepcopy(replay)
    entries = reordered_replay["dictionary"]
    entries[0], entries[different] = entries[different], entries[0]
    cases = [
        ("valid_control", raw, manifest, replay, None),
        ("legacy_trimmed_dictionary", trimmed, manifest, replay, "Dictionary SHA-256 does not match the generated manifest"),
        ("legacy_trimmed_dictionary_and_forged_manifest", trimmed, forged_trimmed, replay, "generated_sha256 mismatch"),
        ("reordered_dictionary_and_forged_manifest", reordered, forged_reordered, replay, "generated_sha256 mismatch"),
        ("wrong_raw_token_count", raw, {**manifest, "raw_token_count": len(tokens) - 1}, replay, "raw_token_count mismatch"),
        ("wrong_revision", raw, {**manifest, "revision": "0" * 40}, replay, "revision mismatch"),
        ("disabled_appended_ascii_space", raw, {**manifest, "use_space_char": False}, replay, "use_space_char mismatch"),
        ("reordered_replay_dictionary", raw, manifest, reordered_replay, "Python and production Core dictionary order differs"),
    ]
    rows = []
    with tempfile.TemporaryDirectory(prefix="phraselayer-dictionary-fault-") as temp:
        root = Path(temp)
        for name, dictionary_bytes, metadata, replay_document, expected_error in cases:
            dictionary_path = root / "dictionary.txt"
            manifest_path = root / "manifest.json"
            replay_path = root / "replay.json"
            result_path = root / "result.json"
            result_path.unlink(missing_ok=True)
            dictionary_path.write_bytes(dictionary_bytes)
            manifest_path.write_text(json.dumps(metadata, ensure_ascii=True), encoding="utf-8")
            replay_path.write_text(json.dumps(replay_document, ensure_ascii=True, allow_nan=False), encoding="utf-8")
            completed = subprocess.run(["dotnet", str(executable), str(replay_path), str(result_path),
                                        str(dictionary_path), str(manifest_path)],
                                       cwd=ROOT, capture_output=True, text=True, timeout=30, check=False)
            if expected_error is None:
                result = json.loads(result_path.read_text()) if result_path.is_file() else {}
                passed = completed.returncode == 0 and result.get("safety_result") == "PASS" and all(
                    result.get(key) is True for key in ("production_dictionary_parser_executed",
                    "production_dictionary_manifest_validated", "python_core_dictionary_order_exact"))
                if not passed:
                    raise RuntimeError("Positive control did not complete; negative results would be inconclusive")
            else:
                passed = completed.returncode != 0 and expected_error in completed.stderr and not result_path.exists()
            rows.append({"name": name, "passed": passed, "expected_rejection": expected_error is not None,
                         "process_exit_code": completed.returncode, "expected_reason_observed": passed if expected_error else None})
    report.update(status="completed", safety_result="PASS" if all(row["passed"] for row in rows) else "FAIL",
                  cases=len(rows), negative_cases=len(rows)-1, failed_checks=sum(not row["passed"] for row in rows),
                  positive_control_passed=True, checks=rows)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dictionary", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--replay", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    report = {"experiment": "production-core-dictionary-identity-fault-injection", "status": "uncertain",
              "scope": "host-CSharp-Core-with-staged-real-dictionary-and-synthetic-input-model-replay",
              "git_commit": os.getenv("GITHUB_SHA"), "python": platform.python_version(),
              "real_unity_execution_performed": False, "quest_execution_performed": False,
              "latency_measured": False, "raw_stderr_uploaded": False, "dictionary_bytes_uploaded": False}
    try:
        run(args, report)
    except Exception as error:
        report.update(status="failed", safety_result="UNCERTAIN", error_type=type(error).__name__)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=True))
    return 0 if report.get("safety_result") == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
