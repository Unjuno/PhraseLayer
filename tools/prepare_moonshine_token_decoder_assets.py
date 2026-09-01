#!/usr/bin/env python3
"""Build the tiny runtime token-decoder asset used by PhraseLayer Listen Mode.

The output intentionally matches Moonshine's native BinTokenizer token-entry format:
entries are stored in token-id order as a 1- or 2-byte length followed by token bytes.
For the reviewed tokenizer contract, `<0xHH>` vocabulary entries are materialized as
one raw byte; all other token spellings are stored as UTF-8. Runtime decoding then
concatenates bytes, skips `<...>` specials, replaces the U+2581 space marker with a
space, and trims the result. No model weights are read or written by this tool.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re
from typing import Any, Dict, List, Mapping

EXPECTED_TOKEN_COUNT = 32768
EXPECTED_BASE_COUNT = 32000
EXPECTED_ADDED_COUNT = 771
SPACE_MARKER = "▁"
BYTE_FALLBACK_RE = re.compile(r"^<0x([0-9A-Fa-f]{2})>$")


class TokenDecoderAssetError(ValueError):
    pass


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise TokenDecoderAssetError(message)


def _load_tokenizer(path: pathlib.Path) -> Mapping[str, Any]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise TokenDecoderAssetError(f"failed to parse tokenizer.json: {exc}") from exc
    _require(isinstance(payload, dict), "tokenizer root must be an object")
    return payload


def _validate_decode_contract(tokenizer: Mapping[str, Any]) -> None:
    model = tokenizer.get("model")
    _require(isinstance(model, dict), "tokenizer model block is missing")
    _require(model.get("type") == "BPE", "reviewed Moonshine tokenizer must use BPE")
    _require(model.get("byte_fallback") is True, "reviewed Moonshine tokenizer requires byte_fallback=true")
    _require(tokenizer.get("pre_tokenizer") is None, "reviewed Moonshine tokenizer must not use a pre-tokenizer")

    normalizer = tokenizer.get("normalizer")
    _require(isinstance(normalizer, dict) and normalizer.get("type") == "Sequence",
             "reviewed Moonshine normalizer must be a Sequence")
    normalizers = normalizer.get("normalizers")
    _require(isinstance(normalizers, list) and len(normalizers) == 2,
             "reviewed Moonshine normalizer sequence drift")
    _require(normalizers[0] == {"type": "Prepend", "prepend": SPACE_MARKER},
             "reviewed Moonshine Prepend normalizer drift")
    _require(normalizers[1] == {
        "type": "Replace",
        "pattern": {"String": " "},
        "content": SPACE_MARKER,
    }, "reviewed Moonshine space normalizer drift")

    decoder = tokenizer.get("decoder")
    _require(isinstance(decoder, dict) and decoder.get("type") == "Sequence",
             "reviewed Moonshine decoder must be a Sequence")
    decoders = decoder.get("decoders")
    _require(isinstance(decoders, list) and len(decoders) == 4,
             "reviewed Moonshine decoder sequence drift")
    _require(decoders[0] == {
        "type": "Replace",
        "pattern": {"String": SPACE_MARKER},
        "content": " ",
    }, "reviewed Moonshine decoder Replace drift")
    _require(decoders[1] == {"type": "ByteFallback"}, "reviewed Moonshine ByteFallback decoder drift")
    _require(decoders[2] == {"type": "Fuse"}, "reviewed Moonshine Fuse decoder drift")
    _require(decoders[3] == {"type": "Strip", "content": " ", "start": 1, "stop": 0},
             "reviewed Moonshine Strip decoder drift")


def build_token_entries(tokenizer: Mapping[str, Any]) -> List[bytes]:
    _validate_decode_contract(tokenizer)
    model = tokenizer["model"]
    vocab = model.get("vocab")
    _require(isinstance(vocab, dict) and len(vocab) == EXPECTED_BASE_COUNT,
             f"Moonshine base vocabulary must contain exactly {EXPECTED_BASE_COUNT} entries")
    entries: List[bytes | None] = [None] * EXPECTED_TOKEN_COUNT

    def assign(token_id: Any, content: Any, origin: str) -> None:
        _require(isinstance(token_id, int) and not isinstance(token_id, bool), f"{origin} token id must be an integer")
        _require(0 <= token_id < EXPECTED_TOKEN_COUNT, f"{origin} token id {token_id} is out of range")
        _require(isinstance(content, str), f"{origin} token content must be a string")
        match = BYTE_FALLBACK_RE.fullmatch(content)
        encoded = bytes([int(match.group(1), 16)]) if match else content.encode("utf-8")
        existing = entries[token_id]
        if existing is not None and existing != encoded:
            raise TokenDecoderAssetError(f"conflicting spelling for token id {token_id} from {origin}")
        entries[token_id] = encoded

    for content, token_id in vocab.items():
        assign(token_id, content, "base vocabulary")

    added = tokenizer.get("added_tokens")
    _require(isinstance(added, list) and len(added) == EXPECTED_ADDED_COUNT,
             f"Moonshine added_tokens must contain exactly {EXPECTED_ADDED_COUNT} entries")
    for index, item in enumerate(added):
        _require(isinstance(item, dict), f"added_tokens[{index}] must be an object")
        _require(item.get("special") is True, f"added_tokens[{index}] must remain special")
        assign(item.get("id"), item.get("content"), f"added_tokens[{index}]")

    missing = [index for index, value in enumerate(entries) if value is None]
    _require(not missing, f"Moonshine token id space is incomplete; first missing id {missing[0] if missing else -1}")
    return [value for value in entries if value is not None]


def _encode_length(length: int) -> bytes:
    _require(0 <= length <= 32767, "token byte length is outside BinTokenizer two-byte range")
    if length == 0:
        return b"\x00"
    if length < 128:
        return bytes([length])
    return bytes([128 + (length % 128), length // 128])


def build_binary(entries: List[bytes]) -> bytes:
    _require(len(entries) == EXPECTED_TOKEN_COUNT,
             f"decoder asset requires exactly {EXPECTED_TOKEN_COUNT} token entries")
    output = bytearray()
    for entry in entries:
        output.extend(_encode_length(len(entry)))
        output.extend(entry)
    return bytes(output)


def prepare(tokenizer_path: pathlib.Path, output_path: pathlib.Path, manifest_path: pathlib.Path) -> Dict[str, Any]:
    tokenizer = _load_tokenizer(tokenizer_path)
    entries = build_token_entries(tokenizer)
    binary = build_binary(entries)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(binary)
    tokenizer_bytes = tokenizer_path.read_bytes()
    manifest = {
        "schema_version": 1,
        "format": "moonshine-bin-tokenizer-compatible-v1",
        "token_count": EXPECTED_TOKEN_COUNT,
        "space_marker": SPACE_MARKER,
        "special_skip_rule": "entry-byte-length>2 && first-byte='<' && last-byte='>'",
        "byte_fallback_rule": "<0xHH> vocabulary spelling becomes one raw byte",
        "source_tokenizer_sha256": hashlib.sha256(tokenizer_bytes).hexdigest(),
        "source_tokenizer_size_bytes": len(tokenizer_bytes),
        "artifact": output_path.name,
        "artifact_size_bytes": len(binary),
        "artifact_sha256": hashlib.sha256(binary).hexdigest(),
    }
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tokenizer", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--manifest", type=pathlib.Path, required=True)
    args = parser.parse_args()
    print(json.dumps(prepare(args.tokenizer, args.output, args.manifest), sort_keys=True))


if __name__ == "__main__":
    main()
