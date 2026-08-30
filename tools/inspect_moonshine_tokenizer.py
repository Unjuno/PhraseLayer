#!/usr/bin/env python3
"""Inspect the structural decoding contract of a Moonshine tokenizer.json without external dependencies."""

from __future__ import annotations

import argparse
import json
import pathlib
from typing import Any, Dict, Mapping, Optional


class TokenizerInspectionError(ValueError):
    pass


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise TokenizerInspectionError(message)


def _component_summary(value: Any) -> Any:
    if value is None:
        return None
    _require(isinstance(value, dict), "tokenizer component must be an object or null")
    component_type = value.get("type")
    _require(isinstance(component_type, str) and component_type, "tokenizer component type is missing")
    result: Dict[str, Any] = {"type": component_type}
    if component_type == "Sequence":
        children = value.get("pretokenizers", value.get("decoders", value.get("normalizers")))
        _require(isinstance(children, list), "Sequence tokenizer component children are missing")
        result["children"] = [_component_summary(child) for child in children]
    for field in (
        "prefix",
        "suffix",
        "replacement",
        "prepend_scheme",
        "cleanup",
        "add_prefix_space",
        "trim_offsets",
        "use_regex",
    ):
        if field in value:
            result[field] = value[field]
    return result


def inspect_tokenizer(tokenizer: Mapping[str, Any]) -> Dict[str, Any]:
    model = tokenizer.get("model")
    _require(isinstance(model, dict), "tokenizer model block is missing")
    model_type = model.get("type")
    _require(isinstance(model_type, str) and model_type, "tokenizer model.type is missing")

    vocab = model.get("vocab")
    if isinstance(vocab, dict):
        base_vocab_size = len(vocab)
        ids = list(vocab.values())
        _require(all(isinstance(item, int) and not isinstance(item, bool) for item in ids),
                 "tokenizer base vocabulary ids must be integers")
    elif isinstance(vocab, list):
        base_vocab_size = len(vocab)
        ids = list(range(base_vocab_size))
    else:
        raise TokenizerInspectionError("tokenizer model.vocab must be an object or list")

    added = tokenizer.get("added_tokens", [])
    _require(isinstance(added, list), "tokenizer added_tokens must be a list")
    added_by_id: Dict[int, str] = {}
    special_count = 0
    for index, item in enumerate(added):
        _require(isinstance(item, dict), f"added_tokens[{index}] must be an object")
        token_id = item.get("id")
        content = item.get("content")
        _require(isinstance(token_id, int) and not isinstance(token_id, bool),
                 f"added_tokens[{index}].id must be an integer")
        _require(isinstance(content, str), f"added_tokens[{index}].content must be a string")
        added_by_id[token_id] = content
        if item.get("special") is True:
            special_count += 1

    interesting_ids = [0, 1, 2, 3, 32000, 32767]
    interesting_tokens = {
        str(token_id): added_by_id[token_id]
        for token_id in interesting_ids
        if token_id in added_by_id
    }

    result: Dict[str, Any] = {
        "model": {
            "type": model_type,
            "base_vocabulary_size": base_vocab_size,
            "minimum_base_id": min(ids) if ids else None,
            "maximum_base_id": max(ids) if ids else None,
        },
        "normalizer": _component_summary(tokenizer.get("normalizer")),
        "pre_tokenizer": _component_summary(tokenizer.get("pre_tokenizer")),
        "post_processor": _component_summary(tokenizer.get("post_processor")),
        "decoder": _component_summary(tokenizer.get("decoder")),
        "added_tokens": {
            "count": len(added),
            "special_count": special_count,
            "interesting_ids": interesting_tokens,
        },
    }

    for field in (
        "unk_token",
        "continuing_subword_prefix",
        "end_of_word_suffix",
        "byte_fallback",
        "fuse_unk",
    ):
        if field in model:
            result["model"][field] = model[field]
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tokenizer", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path)
    args = parser.parse_args()

    try:
        tokenizer = json.loads(args.tokenizer.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise SystemExit(f"failed to load tokenizer.json: {exc}") from exc
    _require(isinstance(tokenizer, dict), "tokenizer root must be an object")
    report = inspect_tokenizer(tokenizer)
    rendered = json.dumps(report, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    if args.output is not None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
