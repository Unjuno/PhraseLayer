#!/usr/bin/env python3
"""Run the deterministic assistance-guard promotion stress experiment and gate integration candidacy.

This is a Core-policy experiment only. It does not measure human learning effectiveness or Quest behavior.
"""

from __future__ import annotations

import json
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "experiments" / "PhraseLayer.AssistanceGuardPromotion" / "PhraseLayer.AssistanceGuardPromotion.csproj"


def main() -> None:
    completed = subprocess.run(
        ["dotnet", "run", "--project", str(PROJECT), "-c", "Release"],
        cwd=ROOT,
        check=False,
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        if completed.stdout:
            print(completed.stdout, end="")
        if completed.stderr:
            print(completed.stderr, end="")
        raise SystemExit(completed.returncode)

    lines = [line.strip() for line in completed.stdout.splitlines() if line.strip()]
    if not lines:
        raise SystemExit("promotion experiment produced no JSON output")
    try:
        data = json.loads(lines[-1])
    except json.JSONDecodeError as error:
        raise SystemExit(f"promotion experiment final line is not JSON: {error}") from error

    production = data["aggregate"]["production"]
    guard = data["aggregate"]["local_guard_allowance_2"]
    cross_source = data["cross_source_counterexample"]
    compact = {
        "status": data.get("status"),
        "experiment": data.get("experiment"),
        "cells_tested": data.get("cells_tested"),
        "cells_where_guard_regresses": data.get("cells_where_guard_regresses"),
        "cells_where_guard_dominates": data.get("cells_where_guard_dominates"),
        "cells_with_actual_guard_intervention": data.get("cells_with_actual_guard_intervention"),
        "candidate_for_product_integration": data.get("candidate_for_product_integration"),
        "production_increase_transitions": production["IncreaseTransitions"],
        "guard_increase_transitions": guard["IncreaseTransitions"],
        "production_increase_rate": production["IncreaseRate"],
        "guard_increase_rate": guard["IncreaseRate"],
        "production_max_increase": production["MaxIncrease"],
        "guard_max_increase": guard["MaxIncrease"],
        "production_mean_target_error": production["MeanAbsoluteTargetError"],
        "guard_mean_target_error": guard["MeanAbsoluteTargetError"],
        "production_profiles_reaching_zero": production["ProfilesReachingZero"],
        "guard_profiles_reaching_zero": guard["ProfilesReachingZero"],
        "production_max_encounters_to_zero": production["MaxEncountersToZero"],
        "guard_max_encounters_to_zero": guard["MaxEncountersToZero"],
        "cross_source_under_assistance_delta": cross_source["UnderAssistanceDelta"],
        "cross_source_global_carry_over_would_under_assist": cross_source["GlobalCarryOverWouldUnderAssist"],
        "human_learning_effectiveness_measured": False,
        "quest_execution_performed": False,
    }
    print(json.dumps(compact, sort_keys=True))

    if data.get("cells_where_guard_regresses") != 0:
        raise SystemExit("local assistance guard regressed at least one fixture/seed cell")
    if not data.get("aggregate_improvement_on_all_reviewed_metrics", True):
        # Older experiment schema computes aggregate candidacy into candidate_for_product_integration.
        raise SystemExit("local assistance guard did not improve/non-regress aggregate reviewed metrics")
    if not data.get("candidate_for_product_integration"):
        raise SystemExit("local assistance guard failed deterministic promotion criteria")
    if not cross_source.get("GlobalCarryOverWouldUnderAssist"):
        raise SystemExit("cross-source anti-hysteresis counterexample must remain active")


if __name__ == "__main__":
    main()
