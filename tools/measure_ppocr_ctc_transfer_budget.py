#!/usr/bin/env python3
"""Host-only transfer-budget experiment for the PP-OCR recognizer CTC boundary.

This measures bytes implied by the pinned class count and reviewed live ABI. It is not a latency,
frame-time, memory-residency, or Quest performance measurement.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FLOAT32_BYTES = 4
CTC_BLANK_CLASSES = 1


class BudgetError(ValueError):
    pass


def _recognition_candidate(lock: dict) -> dict:
    matches = [candidate for candidate in lock.get("candidates", []) if candidate.get("purpose") == "ocr-recognition"]
    if len(matches) != 1:
        raise BudgetError(f"expected exactly one ocr-recognition candidate, found {len(matches)}")
    return matches[0]


def _numeric_constant(source: str, name: str, label: str) -> int:
    match = re.search(rf"public const int {re.escape(name)}\s*=\s*(\d+)\s*;", source)
    if match is None:
        raise BudgetError(f"missing numeric {label} constant: {name}")
    return int(match.group(1))


def measure(root: Path = ROOT) -> dict[str, object]:
    lock = json.loads((root / "models" / "models.lock.json").read_text(encoding="utf-8"))
    core_abi = (root / "src" / "PhraseLayer.Core" / "PaddleCtcPackedOutput.cs").read_text(encoding="utf-8")
    runtime = (root / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts" / "UnityPaddleOcrRecognizerRuntime.cs").read_text(encoding="utf-8")
    candidate = _recognition_candidate(lock)
    dictionary = candidate.get("recognition_dictionary")
    if not isinstance(dictionary, dict):
        raise BudgetError("recognition candidate is missing recognition_dictionary")

    token_count = dictionary.get("effective_token_count")
    if not isinstance(token_count, int) or token_count <= 0:
        raise BudgetError("effective_token_count must be a positive integer")

    values_per_timestep = _numeric_constant(core_abi, "ValuesPerTimestep", "Core packed ABI")
    readbacks_per_crop = _numeric_constant(runtime, "ReducedReadbackOperationsPerCrop", "runtime")
    wiring = "public const int ReducedValuesPerTimestep = PaddleCtcPackedOutput.ValuesPerTimestep;"
    if wiring not in runtime:
        raise BudgetError("Unity recognizer runtime no longer aliases ReducedValuesPerTimestep to the Core packed ABI")

    class_count = token_count + CTC_BLANK_CLASSES
    full_bytes_per_timestep = class_count * FLOAT32_BYTES
    packed_bytes_per_timestep = values_per_timestep * FLOAT32_BYTES
    if packed_bytes_per_timestep <= 0:
        raise BudgetError("packed transfer must be positive")

    return {
        "status": "pass",
        "experiment": "ppocr-recognizer-ctc-transfer-budget",
        "scope": "host-formula-only",
        "recognizer_revision": candidate.get("revision"),
        "dictionary_token_count": token_count,
        "ctc_class_count": class_count,
        "float32_bytes": FLOAT32_BYTES,
        "full_probability_values_per_timestep": class_count,
        "packed_values_per_timestep": values_per_timestep,
        "full_probability_bytes_per_timestep": full_bytes_per_timestep,
        "packed_bytes_per_timestep": packed_bytes_per_timestep,
        "payload_reduction_ratio": full_bytes_per_timestep / packed_bytes_per_timestep,
        "explicit_output_readbacks_per_crop": readbacks_per_crop,
        "runtime_values_per_timestep_aliases_core_abi": True,
        "full_probability_matrix_cpu_readback": False,
        "latency_measured": False,
        "quest_execution_performed": False,
        "quest_performance_claim_allowed": False,
    }


def main() -> None:
    print(json.dumps(measure(), sort_keys=True))


if __name__ == "__main__":
    main()
