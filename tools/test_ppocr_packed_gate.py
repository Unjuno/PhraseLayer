#!/usr/bin/env python3
"""Regression tests for the static guard itself; no GPU execution is implied."""
from __future__ import annotations
import unittest
import validate_ppocr_gpu_ctc_reduction_gate as gate


class PackedGateTests(unittest.TestCase):
    def test_current_wiring(self):
        self.assertEqual(gate.validate()["scope"], "static-wiring-only")

    def test_rejects_second_readback(self):
        runtime = gate.RUNTIME.read_text(encoding="utf-8")
        mutation = runtime.replace("var packedCpu = packedTensor.ReadbackAndClone();",
            "var packedCpu = packedTensor.ReadbackAndClone();\nprobabilityTensor.ReadbackAndClone();")
        with self.assertRaises(gate.GateError):
            gate.validate_live_readback(mutation)

    def test_rejects_wrong_readback_source(self):
        runtime = gate.RUNTIME.read_text(encoding="utf-8").replace(
            "var packedCpu = packedTensor.ReadbackAndClone();", "var packedCpu = probabilityTensor.ReadbackAndClone();")
        with self.assertRaises(gate.GateError):
            gate.validate_live_readback(runtime)

    def test_commented_shape_guard_does_not_pass(self):
        runtime = gate.RUNTIME.read_text(encoding="utf-8").replace(
            "PaddleCtcPackedOutput.ValidateShapes(outputShape, packedShape);",
            "// PaddleCtcPackedOutput.ValidateShapes(outputShape, packedShape);")
        with self.assertRaises(gate.GateError):
            gate.validate_live_readback(runtime)

    def test_comment_readback_decoy_is_ignored(self):
        runtime = gate.RUNTIME.read_text(encoding="utf-8").replace(
            "var packedCpu = packedTensor.ReadbackAndClone();",
            "// probabilityTensor.ReadbackAndClone();\nvar packedCpu = packedTensor.ReadbackAndClone();")
        gate.validate_live_readback(runtime)

    def test_missing_method_fails(self):
        with self.assertRaises(gate.GateError):
            gate.validate_live_readback("// public PaddleRecognizerReducedOutput ExecuteReduced() {}")

    def test_graphics_flag_in_comment_is_allowed(self):
        gate.validate_graphics_shell('# Intentionally no -nographics\n"$UNITY_EDITOR" -batchmode\n')

    def test_same_line_quoted_and_continued_graphics_flags_are_rejected(self):
        for args in ('-nographics', '"-nographics"', "'-nographics'", "\\\n  -nographics"):
            with self.subTest(args=args), self.assertRaises(gate.GateError):
                gate.validate_graphics_shell('"$UNITY_EDITOR" -batchmode ' + args)


if __name__ == "__main__":
    unittest.main()
