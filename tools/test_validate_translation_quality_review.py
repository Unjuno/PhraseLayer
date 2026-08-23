#!/usr/bin/env python3
from __future__ import annotations

import copy
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

from validate_translation_quality_review import load_json, validate_review  # noqa: E402


CORPUS = load_json(ROOT / "benchmarks" / "translation-quality-corpus.json")
MODELS = load_json(ROOT / "models" / "models.lock.json")
MODEL = next(item for item in MODELS["candidates"] if item.get("id") == "opus-mt-en-jap")


def passing_review() -> dict:
    return {
        "schema_version": 1,
        "candidate_id": MODEL["id"],
        "candidate_revision": MODEL["revision"],
        "corpus_schema_version": CORPUS["schema_version"],
        "language_pair": CORPUS["language_pair"],
        "reviewer_mode": "human-structured",
        "notes": "synthetic structural test only",
        "reviews": [
            {
                "case_id": item["id"],
                "source": item["source"],
                "candidate": "synthetic candidate output",
                "severity": "None",
                "failed_dimensions": [],
                "notes": "synthetic pass for validator regression",
            }
            for item in CORPUS["cases"]
        ],
    }


def assert_contains(errors: list[str], fragment: str) -> None:
    if not any(fragment in error for error in errors):
        raise AssertionError(f"expected error containing {fragment!r}; got {errors!r}")


def main() -> int:
    review = passing_review()
    errors, summary = validate_review(review, CORPUS, MODELS)
    if errors:
        raise AssertionError(f"synthetic complete pass should validate: {errors}")
    if not summary["promotion_policy_passed"]:
        raise AssertionError("synthetic complete pass should satisfy policy")
    if summary["reviewed_cases"] != len(CORPUS["cases"]):
        raise AssertionError("complete review count mismatch")

    incomplete = passing_review()
    incomplete["reviews"] = incomplete["reviews"][:-1]
    errors, summary = validate_review(incomplete, CORPUS, MODELS)
    assert_contains(errors, "review is incomplete")
    if summary["promotion_policy_passed"]:
        raise AssertionError("incomplete review must not pass promotion policy")

    critical = passing_review()
    critical["reviews"][0]["severity"] = "Critical"
    critical["reviews"][0]["failed_dimensions"] = ["NegationPolarity"]
    errors, summary = validate_review(critical, CORPUS, MODELS)
    assert_contains(errors, "promotion policy failed")
    if summary["critical_failures"] != 1:
        raise AssertionError("critical failure count mismatch")

    bad_source = passing_review()
    bad_source["reviews"][0]["source"] = "different source"
    errors, _ = validate_review(bad_source, CORPUS, MODELS)
    assert_contains(errors, "source text must exactly match corpus")

    bad_revision = passing_review()
    bad_revision["candidate_revision"] = "not-the-pinned-revision"
    errors, _ = validate_review(bad_revision, CORPUS, MODELS)
    assert_contains(errors, "candidate revision mismatch")

    bad_dimensions = passing_review()
    bad_dimensions["reviews"][0]["severity"] = "Major"
    bad_dimensions["reviews"][0]["failed_dimensions"] = []
    errors, _ = validate_review(bad_dimensions, CORPUS, MODELS)
    assert_contains(errors, "must identify at least one failed dimension")

    work_in_progress = passing_review()
    work_in_progress["reviews"] = work_in_progress["reviews"][:2]
    errors, summary = validate_review(
        work_in_progress,
        CORPUS,
        MODELS,
        require_complete=False,
        enforce_promotion_policy=False,
    )
    if errors:
        raise AssertionError(f"work-in-progress subset should be structurally valid: {errors}")
    if summary["reviewed_cases"] != 2:
        raise AssertionError("work-in-progress review count mismatch")

    print("PASS: translation quality review validator regression suite")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
