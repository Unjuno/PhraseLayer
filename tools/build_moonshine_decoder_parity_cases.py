#!/usr/bin/env python3
"""Build exact-tokenizer decode parity cases for the managed Moonshine decoder."""

from __future__ import annotations

import argparse
import json
import pathlib
from typing import Any, Dict, List

TEXT_CASES = [
    ("plain", "Hello world!"),
    ("punctuation", "Wait... really? $9.99."),
    ("accent-byte-fallback", "café déjà vu"),
    ("emoji-byte-fallback", "hello 👋 world"),
    ("non-latin-byte-fallback", "Tokyo 東京 Station"),
    ("multiple-spaces", "one  two   three"),
]


def build_cases(tokenizer_path: pathlib.Path) -> List[Dict[str, Any]]:
    try:
        from tokenizers import Tokenizer
    except ImportError as exc:
        raise RuntimeError("tokenizers is required for live Moonshine decoder parity") from exc

    tokenizer = Tokenizer.from_file(str(tokenizer_path))
    cases: List[Dict[str, Any]] = []
    for name, text in TEXT_CASES:
        encoded = tokenizer.encode(text, add_special_tokens=False)
        ids = list(encoded.ids)
        expected = tokenizer.decode(ids, skip_special_tokens=True)
        cases.append({"name": name, "ids": ids, "expected": expected})

        # ASR generation can carry BOS/EOS and timestamp-control tokens. The production decoder must
        # ignore those while preserving exactly the same text bytes.
        controlled = [1] + ids + [32000, 32767, 2]
        cases.append({
            "name": name + "-with-specials",
            "ids": controlled,
            "expected": tokenizer.decode(controlled, skip_special_tokens=True),
        })
    return cases


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tokenizer", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    cases = build_cases(args.tokenizer)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(cases, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"cases": len(cases)}, sort_keys=True))


if __name__ == "__main__":
    main()
