#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import json
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "tools" / "measure_ppocr_ctc_transfer_budget.py"
spec = importlib.util.spec_from_file_location("measure_ppocr_ctc_transfer_budget", MODULE_PATH)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)


def main() -> None:
    data = module.measure()
    assert data["status"] == "pass"
    assert data["dictionary_token_count"] == 6905
    assert data["ctc_class_count"] == 6906
    assert data["full_probability_values_per_timestep"] == 6906
    assert data["packed_values_per_timestep"] == 2
    assert data["full_probability_bytes_per_timestep"] == 27624
    assert data["packed_bytes_per_timestep"] == 8
    assert data["payload_reduction_ratio"] == 3453.0
    assert data["explicit_output_readbacks_per_crop"] == 1
    assert data["runtime_values_per_timestep_aliases_core_abi"] is True
    assert data["full_probability_matrix_cpu_readback"] is False
    assert data["latency_measured"] is False
    assert data["quest_execution_performed"] is False
    assert data["quest_performance_claim_allowed"] is False

    source = MODULE_PATH.read_text(encoding="utf-8")
    assert '"scope": "host-formula-only"' in source
    assert '"latency_measured": False' in source
    assert '"quest_execution_performed": False' in source
    assert '"quest_performance_claim_allowed": False' in source
    assert "effective_token_count" in source
    assert "PaddleCtcPackedOutput.cs" in source
    assert "PaddleCtcPackedOutput.ValuesPerTimestep" in source
    assert '"ReducedReadbackOperationsPerCrop"' in source

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "models").mkdir(parents=True)
        core_dir = root / "src" / "PhraseLayer.Core"
        core_dir.mkdir(parents=True)
        runtime_dir = root / "unity" / "PhraseLayer.Unity" / "Assets" / "Scripts"
        runtime_dir.mkdir(parents=True)
        lock = {
            "candidates": [
                {
                    "purpose": "ocr-recognition",
                    "revision": "a" * 40,
                    "recognition_dictionary": {"effective_token_count": 3},
                }
            ]
        }
        (root / "models" / "models.lock.json").write_text(json.dumps(lock), encoding="utf-8")
        (core_dir / "PaddleCtcPackedOutput.cs").write_text(
            "public const int ValuesPerTimestep = 2;\n",
            encoding="utf-8",
        )
        (runtime_dir / "UnityPaddleOcrRecognizerRuntime.cs").write_text(
            "public const int ReducedValuesPerTimestep = PaddleCtcPackedOutput.ValuesPerTimestep;\n"
            "public const int ReducedReadbackOperationsPerCrop = 1;\n",
            encoding="utf-8",
        )
        fixture = module.measure(root)
        assert fixture["ctc_class_count"] == 4
        assert fixture["full_probability_bytes_per_timestep"] == 16
        assert fixture["packed_bytes_per_timestep"] == 8
        assert fixture["payload_reduction_ratio"] == 2.0

        (core_dir / "PaddleCtcPackedOutput.cs").write_text(
            "public const int ValuesPerTimestep = 3;\n",
            encoding="utf-8",
        )
        (runtime_dir / "UnityPaddleOcrRecognizerRuntime.cs").write_text(
            "public const int ReducedValuesPerTimestep = PaddleCtcPackedOutput.ValuesPerTimestep;\n"
            "public const int ReducedReadbackOperationsPerCrop = 2;\n",
            encoding="utf-8",
        )
        drifted = module.measure(root)
        assert drifted["packed_values_per_timestep"] == 3
        assert drifted["explicit_output_readbacks_per_crop"] == 2
        assert drifted["payload_reduction_ratio"] != 2.0

        (runtime_dir / "UnityPaddleOcrRecognizerRuntime.cs").write_text(
            "public const int ReducedValuesPerTimestep = 3;\n"
            "public const int ReducedReadbackOperationsPerCrop = 1;\n",
            encoding="utf-8",
        )
        try:
            module.measure(root)
        except module.BudgetError:
            pass
        else:
            raise AssertionError("runtime must alias the Core packed ABI instead of duplicating the value")

    print("PASS: PP-OCR CTC transfer-budget experiment arithmetic, Core/runtime wiring, and no-device-claim boundary")


if __name__ == "__main__":
    main()
