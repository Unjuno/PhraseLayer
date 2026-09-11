#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "tools/generate_marian_reference_fixture.py"
spec = importlib.util.spec_from_file_location("generate_marian_reference_fixture", MODULE_PATH)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)


def expect_raises(fn, exc_type) -> None:
    try:
        fn()
    except exc_type:
        return
    raise AssertionError(f"expected {exc_type.__name__}")


def main() -> None:
    assert module._strip_decoder_start([46275, 120, 321, 0]) == [120, 321, 0]
    expect_raises(lambda: module._strip_decoder_start([]), module.ReferenceFixtureError)
    expect_raises(lambda: module._strip_decoder_start([9, 0]), module.ReferenceFixtureError)
    expect_raises(lambda: module._strip_decoder_start([46275]), module.ReferenceFixtureError)
    expect_raises(lambda: module._strip_decoder_start([46275, 7]), module.ReferenceFixtureError)
    expect_raises(lambda: module._strip_decoder_start([46275, 46275, 0]), module.ReferenceFixtureError)

    module._validate_limits(128, 64)
    expect_raises(lambda: module._validate_limits(0, 64), module.ReferenceFixtureError)
    expect_raises(lambda: module._validate_limits(128, 0), module.ReferenceFixtureError)

    source = MODULE_PATH.read_text(encoding="utf-8")
    for marker in (
        'HF_HUB_OFFLINE"] = "1"',
        'TRANSFORMERS_OFFLINE"] = "1"',
        'model.to("cpu")',
        'model.eval()',
        'num_beams=1',
        'do_sample=False',
        'bad_words_ids=[[EXPECTED_PAD_TOKEN_ID]]',
        'forced_eos_token_id=EXPECTED_EOS_TOKEN_ID',
        'renormalize_logits=True',
        'source_token_ids',
        'generated_token_ids',
        'source_weight_sha256',
        'phrase-layer-marian-greedy-reference',
    ):
        assert marker in source, marker
    assert "from_pretrained(str(source_dir), local_files_only=True)" in source

    print("PASS: offline Marian greedy reference policy and anti-download markers")


if __name__ == "__main__":
    main()
