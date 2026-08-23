#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CORPUS = ROOT / "benchmarks" / "translation-quality-corpus.json"
DEFAULT_MODELS = ROOT / "models" / "models.lock.json"

SEVERITIES = {"None", "Minor", "Major", "Critical"}
DIMENSIONS = {
    "Adequacy",
    "NegationPolarity",
    "NamedEntity",
    "MultiwordExpression",
    "Modality",
    "TemporalAspect",
    "Quantity",
    "JapaneseReadability",
}


def load_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ValueError(f"missing JSON file: {path}") from exc
    except json.JSONDecodeError as exc:
        raise ValueError(f"invalid JSON in {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise ValueError(f"top-level JSON value must be an object: {path}")
    return value


def expected_candidate(models: dict[str, Any], candidate_id: str) -> dict[str, Any]:
    candidates = models.get("candidates")
    if not isinstance(candidates, list):
        raise ValueError("models.lock.json candidates must be an array")
    matches = [item for item in candidates if isinstance(item, dict) and item.get("id") == candidate_id]
    if len(matches) != 1:
        raise ValueError(f"expected exactly one model candidate named {candidate_id!r}, found {len(matches)}")
    return matches[0]


def validate_review(
    review: dict[str, Any],
    corpus: dict[str, Any],
    models: dict[str, Any],
    *,
    require_complete: bool = True,
    enforce_promotion_policy: bool = True,
) -> tuple[list[str], dict[str, Any]]:
    errors: list[str] = []

    def require(condition: bool, message: str) -> None:
        if not condition:
            errors.append(message)

    require(review.get("schema_version") == 1, "review schema_version must be 1")
    require(corpus.get("schema_version") == 1, "corpus schema_version must be 1")
    require(review.get("corpus_schema_version") == corpus.get("schema_version"), "review corpus_schema_version must match corpus")
    require(review.get("language_pair") == corpus.get("language_pair") == "en-ja", "review/corpus language_pair must be en-ja")
    require(review.get("reviewer_mode") == "human-structured", "review reviewer_mode must be human-structured")

    candidate_id = review.get("candidate_id")
    candidate_revision = review.get("candidate_revision")
    require(isinstance(candidate_id, str) and bool(candidate_id.strip()), "review candidate_id is required")
    require(isinstance(candidate_revision, str) and bool(candidate_revision.strip()), "review candidate_revision is required")
    if isinstance(candidate_id, str) and candidate_id.strip():
        try:
            model = expected_candidate(models, candidate_id.strip())
        except ValueError as exc:
            errors.append(str(exc))
        else:
            require(
                candidate_revision == model.get("revision"),
                f"candidate revision mismatch: review={candidate_revision!r} models.lock={model.get('revision')!r}",
            )
            require(model.get("purpose") == "translation-en-ja", "review candidate must be the translation-en-ja model")

    raw_cases = corpus.get("cases")
    require(isinstance(raw_cases, list) and len(raw_cases) > 0, "corpus cases must be a non-empty array")
    cases_by_id: dict[str, dict[str, Any]] = {}
    if isinstance(raw_cases, list):
        for index, item in enumerate(raw_cases):
            if not isinstance(item, dict):
                errors.append(f"corpus case[{index}] must be an object")
                continue
            case_id = item.get("id")
            if not isinstance(case_id, str) or not case_id:
                errors.append(f"corpus case[{index}] id is required")
                continue
            if case_id in cases_by_id:
                errors.append(f"duplicate corpus case id: {case_id}")
                continue
            cases_by_id[case_id] = item

    raw_reviews = review.get("reviews")
    require(isinstance(raw_reviews, list), "review reviews must be an array")
    reviews_by_id: dict[str, dict[str, Any]] = {}
    severity_counts = {name: 0 for name in SEVERITIES}
    failure_dimension_counts = {name: 0 for name in DIMENSIONS}

    if isinstance(raw_reviews, list):
        for index, item in enumerate(raw_reviews):
            label = f"review[{index}]"
            if not isinstance(item, dict):
                errors.append(f"{label} must be an object")
                continue
            case_id = item.get("case_id")
            if not isinstance(case_id, str) or not case_id:
                errors.append(f"{label} case_id is required")
                continue
            if case_id in reviews_by_id:
                errors.append(f"duplicate review case_id: {case_id}")
                continue
            reviews_by_id[case_id] = item
            case = cases_by_id.get(case_id)
            if case is None:
                errors.append(f"review references unknown corpus case: {case_id}")
                continue

            source = item.get("source")
            candidate = item.get("candidate")
            severity = item.get("severity")
            failed_dimensions = item.get("failed_dimensions")
            notes = item.get("notes")

            require(source == case.get("source"), f"{label} source text must exactly match corpus case {case_id}")
            require(isinstance(candidate, str), f"{label} candidate must be a string")
            require(severity in SEVERITIES, f"{label} severity must be one of {sorted(SEVERITIES)}")
            require(isinstance(failed_dimensions, list), f"{label} failed_dimensions must be an array")
            require(isinstance(notes, str), f"{label} notes must be a string")

            if severity in SEVERITIES:
                severity_counts[severity] += 1

            if isinstance(failed_dimensions, list):
                seen_dimensions: set[str] = set()
                for dimension in failed_dimensions:
                    if dimension not in DIMENSIONS:
                        errors.append(f"{label} contains unknown failed dimension: {dimension!r}")
                        continue
                    if dimension in seen_dimensions:
                        errors.append(f"{label} duplicates failed dimension: {dimension}")
                        continue
                    seen_dimensions.add(dimension)
                    failure_dimension_counts[dimension] += 1

                if severity == "None" and seen_dimensions:
                    errors.append(f"{label} passing severity None cannot contain failed dimensions")
                if severity in {"Minor", "Major", "Critical"} and not seen_dimensions:
                    errors.append(f"{label} failing severity {severity} must identify at least one failed dimension")

    missing = sorted(set(cases_by_id) - set(reviews_by_id))
    if require_complete and missing:
        errors.append("review is incomplete; missing case ids: " + ", ".join(missing))

    reviewed = len([case_id for case_id in reviews_by_id if case_id in cases_by_id])
    critical = severity_counts["Critical"]
    major = severity_counts["Major"]
    minor = severity_counts["Minor"]
    major_or_worse_rate = ((critical + major) / reviewed) if reviewed else 0.0

    policy = corpus.get("policy")
    if not isinstance(policy, dict):
        errors.append("corpus policy must be an object")
        policy = {}
    allowed_critical = policy.get("critical_failures_allowed", 0)
    max_major_rate = policy.get("max_major_or_worse_rate", 0.05)
    complete_required = policy.get("complete_review_required", True)

    policy_passed = (
        (not complete_required or not missing)
        and isinstance(allowed_critical, int)
        and critical <= allowed_critical
        and isinstance(max_major_rate, (int, float))
        and major_or_worse_rate <= float(max_major_rate)
    )
    if enforce_promotion_policy and not policy_passed:
        errors.append(
            "promotion policy failed: "
            f"missing={len(missing)} critical={critical}/{allowed_critical} "
            f"major_or_worse_rate={major_or_worse_rate:.3f}/{max_major_rate}"
        )

    summary = {
        "candidate_id": candidate_id,
        "candidate_revision": candidate_revision,
        "total_cases": len(cases_by_id),
        "reviewed_cases": reviewed,
        "missing_case_ids": missing,
        "critical_failures": critical,
        "major_failures": major,
        "minor_failures": minor,
        "major_or_worse_rate": round(major_or_worse_rate, 6),
        "failed_dimension_counts": {
            key: failure_dimension_counts[key]
            for key in sorted(failure_dimension_counts)
            if failure_dimension_counts[key] > 0
        },
        "promotion_policy_passed": policy_passed,
    }
    return errors, summary


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Validate PhraseLayer structured human translation-quality review evidence."
    )
    parser.add_argument("review", type=Path, help="Review evidence JSON file")
    parser.add_argument("--corpus", type=Path, default=DEFAULT_CORPUS)
    parser.add_argument("--models", type=Path, default=DEFAULT_MODELS)
    parser.add_argument(
        "--allow-incomplete",
        action="store_true",
        help="Allow a work-in-progress subset of the corpus; promotion policy is not enforced.",
    )
    parser.add_argument("--summary", type=Path, help="Optional path to write a normalized JSON summary")
    args = parser.parse_args()

    try:
        review = load_json(args.review)
        corpus = load_json(args.corpus)
        models = load_json(args.models)
        errors, summary = validate_review(
            review,
            corpus,
            models,
            require_complete=not args.allow_incomplete,
            enforce_promotion_policy=not args.allow_incomplete,
        )
    except ValueError as exc:
        print(f"ERROR: {exc}")
        return 1

    if args.summary:
        args.summary.parent.mkdir(parents=True, exist_ok=True)
        args.summary.write_text(json.dumps(summary, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print(json.dumps(summary, indent=2, ensure_ascii=False))
    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1

    print("PASS: structured translation-quality review evidence satisfies the requested gate")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
