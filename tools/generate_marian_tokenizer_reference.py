#!/usr/bin/env python3
"""Generate trusted MarianTokenizer source-tokenization reference data from an exact local snapshot.

This tool never downloads model data. The caller must provide the already validated small snapshot.
No model weights are required for tokenizer parity.
"""

from __future__ import annotations

import argparse
import importlib.metadata
import json
import pathlib
from typing import Any

MODEL_ID = "Helsinki-NLP/opus-mt-en-jap"
EXPECTED_TOOLCHAIN = {
    "transformers": "4.57.6",
    "sentencepiece": "0.2.2",
    "sacremoses": "0.2.0",
}


class ReferenceError(ValueError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ReferenceError(message)


def package_versions() -> dict[str, str]:
    versions: dict[str, str] = {}
    for package, expected in EXPECTED_TOOLCHAIN.items():
        try:
            actual = importlib.metadata.version(package)
        except importlib.metadata.PackageNotFoundError as exc:
            raise ReferenceError(f"missing tokenizer parity dependency: {package}") from exc
        require(actual == expected, f"{package} expected {expected} but found {actual}")
        versions[package] = actual
    return versions


def load_corpus(path: pathlib.Path) -> list[dict[str, str]]:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ReferenceError(f"failed to read corpus {path}: {exc}") from exc
    require(isinstance(document, dict), "parity corpus must contain a JSON object")
    require(document.get("schema_version") == 1, "unsupported parity corpus schema")
    cases = document.get("cases")
    require(isinstance(cases, list) and len(cases) > 0, "parity corpus cases must be non-empty")
    output: list[dict[str, str]] = []
    seen: set[str] = set()
    for index, item in enumerate(cases):
        require(isinstance(item, dict), f"cases[{index}] must be an object")
        case_id = item.get("id")
        text = item.get("text")
        require(isinstance(case_id, str) and case_id, f"cases[{index}].id is invalid")
        require(case_id not in seen, f"duplicate parity case id: {case_id}")
        require(isinstance(text, str) and len(text) > 0, f"cases[{index}].text is invalid")
        seen.add(case_id)
        output.append({"id": case_id, "text": text})
    return output


def generate_reference(snapshot_dir: pathlib.Path, corpus_path: pathlib.Path, revision: str) -> dict[str, Any]:
    require(snapshot_dir.is_dir(), f"snapshot directory does not exist: {snapshot_dir}")
    for name in ("source.spm", "target.spm", "vocab.json", "tokenizer_config.json"):
        require((snapshot_dir / name).is_file(), f"missing tokenizer artifact: {snapshot_dir / name}")
    require(len(revision) == 40 and all(char in "0123456789abcdef" for char in revision),
            "revision must be a full lowercase 40-character SHA")

    versions = package_versions()
    from transformers import MarianTokenizer

    tokenizer = MarianTokenizer.from_pretrained(
        str(snapshot_dir),
        local_files_only=True,
        source_lang="en",
        target_lang="jap",
    )
    require(tokenizer.eos_token_id == 0, f"unexpected EOS token id: {tokenizer.eos_token_id}")
    require(tokenizer.pad_token_id == 46275, f"unexpected PAD token id: {tokenizer.pad_token_id}")

    cases: list[dict[str, Any]] = []
    for item in load_corpus(corpus_path):
        text = item["text"]
        pieces = tokenizer.tokenize(text)
        encoded = tokenizer(
            text,
            add_special_tokens=True,
            return_attention_mask=False,
            return_token_type_ids=False,
        )
        input_ids = encoded["input_ids"]
        require(isinstance(input_ids, list) and input_ids, f"reference returned no ids for {item['id']}")
        require(input_ids[-1] == 0, f"reference EOS mismatch for {item['id']}")
        cases.append(
            {
                "id": item["id"],
                "text": text,
                "pieces": list(pieces),
                "input_ids": list(input_ids),
            }
        )

    return {
        "schema_version": 1,
        "model_id": MODEL_ID,
        "revision": revision,
        "source_language": "en",
        "target_language": "jap",
        "reference": "transformers.MarianTokenizer",
        "toolchain": versions,
        "case_count": len(cases),
        "cases": cases,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--snapshot-dir", type=pathlib.Path, required=True)
    parser.add_argument("--corpus", type=pathlib.Path, required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()

    manifest = generate_reference(args.snapshot_dir, args.corpus, args.revision)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(json.dumps({"case_count": manifest["case_count"], "revision": manifest["revision"]}, sort_keys=True))


if __name__ == "__main__":
    main()
