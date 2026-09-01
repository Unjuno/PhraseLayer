#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import pathlib
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
PATH = ROOT / "tools/run_marian_onnx_reference.py"
spec = importlib.util.spec_from_file_location("run_marian_onnx_reference", PATH)
assert spec is not None and spec.loader is not None
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class Meta:
    def __init__(self, name: str):
        self.name = name


class Session:
    def __init__(self, inputs, outputs):
        self._inputs = [Meta(name) for name in inputs]
        self._outputs = [Meta(name) for name in outputs]

    def get_inputs(self):
        return self._inputs

    def get_outputs(self):
        return self._outputs


def initial_outputs():
    values = ["logits"]
    for layer in range(module.DECODER_LAYERS):
        for kind in ("decoder", "encoder"):
            for key_or_value in ("key", "value"):
                values.append(module._present(layer, kind, key_or_value))
    return values


def cached_inputs():
    values = ["input_ids", "encoder_hidden_states", "encoder_attention_mask"]
    for layer in range(module.DECODER_LAYERS):
        for kind in ("decoder", "encoder"):
            for key_or_value in ("key", "value"):
                values.append(module._past(layer, kind, key_or_value))
    return values


def cached_outputs():
    values = ["logits"]
    for layer in range(module.DECODER_LAYERS):
        for key_or_value in ("key", "value"):
            values.append(module._present(layer, "decoder", key_or_value))
    return values


def expect_failure(fn, contains: str):
    try:
        fn()
    except module.ReferenceError as exc:
        assert contains in str(exc), (contains, str(exc))
    else:
        raise AssertionError("expected ReferenceError")


def main() -> None:
    encoder = Session(["input_ids", "attention_mask"], ["last_hidden_state"])
    decoder = Session(
        ["input_ids", "encoder_hidden_states", "encoder_attention_mask"],
        initial_outputs(),
    )
    cached = Session(cached_inputs(), cached_outputs())
    names = module._validate_names(encoder, decoder, cached)
    assert len(names["cached_inputs"]) == 27
    assert len(names["decoder_outputs"]) == 25

    bad = Session(cached_inputs()[:-1], cached_outputs())
    expect_failure(lambda: module._validate_names(encoder, decoder, bad), "past cache ABI drift")

    class FakeNp:
        inf = float("inf")

        @staticmethod
        def argmax(values):
            return max(range(len(values)), key=lambda index: values[index])

        @staticmethod
        def partition(values, index):
            return sorted(values)

        @staticmethod
        def isfinite(value):
            if isinstance(value, list):
                return [v not in (float("inf"), float("-inf")) for v in value]
            return value not in (float("inf"), float("-inf"))

    # Use a tiny numpy-compatible fixture only when numpy is available in the normal Python environment.
    try:
        import numpy as np
    except ImportError:
        np = None
    if np is not None:
        logits = np.asarray([0.0, 2.0, 3.0, 1.0], dtype=np.float64)
        old_pad = module.PAD_TOKEN_ID
        try:
            module.PAD_TOKEN_ID = 2
            selected, selected_logit, runner_up, margin = module._choose(logits, np)
            assert selected == 1
            assert selected_logit == 2.0
            assert runner_up == 1.0
            assert margin == 1.0
        finally:
            module.PAD_TOKEN_ID = old_pad

    with tempfile.TemporaryDirectory() as temp:
        root = pathlib.Path(temp)
        report = {
            "token_ids": [7, 8, module.EOS_TOKEN_ID],
            "terminated_by_eos": True,
            "decoder_steps": 3,
            "provider": "CPUExecutionProvider",
            "steps": [
                {"step": 0, "selected_token_id": 7, "argmax_margin": 2.0},
                {"step": 1, "selected_token_id": 8, "argmax_margin": 1.0},
                {"step": 2, "selected_token_id": module.EOS_TOKEN_ID, "argmax_margin": 3.0},
            ],
        }
        written = module.write_outputs(
            root / "trace.json",
            root / "tokens.txt",
            root / "translation.txt",
            module.SOURCE_TEXT,
            [11, module.EOS_TOKEN_ID],
            report,
            [7, 8, module.EOS_TOKEN_ID],
            "テスト",
        )
        assert written["pytorch_exact_token_parity"] is True
        assert written["minimum_argmax_margin"] == 1.0
        assert (root / "tokens.txt").read_text(encoding="utf-8") == "7\n8\n0\n"
        expect_failure(
            lambda: module.write_outputs(
                root / "bad.json",
                root / "bad.tokens",
                root / "bad.txt",
                module.SOURCE_TEXT,
                [11, 0],
                report,
                [7, 9, 0],
                "テスト",
            ),
            "differs from pinned PyTorch",
        )

    print("PASS: Marian ONNX reference helper fixtures")


if __name__ == "__main__":
    main()
