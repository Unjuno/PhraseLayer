#!/usr/bin/env python3
"""Generate an offline trusted greedy-reference fixture for the pinned Marian en->ja snapshot.

The script never downloads model data. It first validates the complete local source snapshot, including the locked
pytorch_model.bin identity, then requires the exact reviewed Transformers/Torch export toolchain. Reference sequences
use the same deliberate PhraseLayer policy as Core: beam=1, no sampling, PAD banned after decoder start, and forced
EOS in the final target slot. The resulting JSON is small and may be staged into Unity; model/tokenizer weights stay
local and git-ignored.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
from typing import Any

DEFAULT_SAMPLES = (
    "keep off",
    "emergency exit",
    "I was tired.",
)
DEFAULT_MAXIMUM_SOURCE_TOKENS = 128
DEFAULT_MAXIMUM_TARGET_TOKENS = 64
EXPECTED_DECODER_START_TOKEN_ID = 46275
EXPECTED_PAD_TOKEN_ID = 46275
EXPECTED_EOS_TOKEN_ID = 0


class ReferenceFixtureError(RuntimeError):
    pass


def _load_local_module(filename: str, module_name: str):
    path = Path(__file__).with_name(filename)
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        raise ReferenceFixtureError(f"failed to load helper module: {filename}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _validate_limits(maximum_source_tokens: int, maximum_target_tokens: int) -> None:
    if maximum_source_tokens <= 0:
        raise ReferenceFixtureError("maximum source tokens must be greater than zero")
    if maximum_target_tokens <= 0:
        raise ReferenceFixtureError("maximum target tokens must be greater than zero")


def _strip_decoder_start(sequence: list[int]) -> list[int]:
    if not sequence:
        raise ReferenceFixtureError("Transformers generated an empty token sequence")
    if sequence[0] != EXPECTED_DECODER_START_TOKEN_ID:
        raise ReferenceFixtureError(
            "Transformers sequence does not begin with the reviewed Marian decoder-start token: "
            f"expected={EXPECTED_DECODER_START_TOKEN_ID} actual={sequence[0]}"
        )
    generated = sequence[1:]
    if not generated:
        raise ReferenceFixtureError("Transformers generated no target token after decoder start")
    if generated[-1] != EXPECTED_EOS_TOKEN_ID:
        raise ReferenceFixtureError(
            "Transformers greedy reference did not terminate with the reviewed EOS token"
        )
    if EXPECTED_PAD_TOKEN_ID in generated:
        raise ReferenceFixtureError(
            "Transformers greedy reference emitted PAD after decoder start despite the reviewed bad-word policy"
        )
    return generated


def generate_fixture(
    source_dir: Path,
    repository_root: Path,
    samples: list[str],
    maximum_source_tokens: int,
    maximum_target_tokens: int,
) -> dict[str, Any]:
    _validate_limits(maximum_source_tokens, maximum_target_tokens)
    if not samples or any(not sample.strip() for sample in samples):
        raise ReferenceFixtureError("reference samples must contain non-empty source text")

    export = _load_local_module("export_marian_onnx.py", "export_marian_onnx_for_reference")
    source = export.validate_local_source_snapshot(
        source_dir,
        repository_root / "models/models.lock.json",
        repository_root,
    )
    toolchain = export.validate_export_toolchain()

    # Force every library involved in model loading to remain offline before importing Transformers.
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_DATASETS_OFFLINE"] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"

    try:
        import torch
        from transformers import MarianMTModel, MarianTokenizer
    except Exception as exc:  # pragma: no cover - exercised on the self-hosted gate
        raise ReferenceFixtureError(f"failed to import reviewed Marian reference runtime: {exc}") from exc

    tokenizer = MarianTokenizer.from_pretrained(str(source_dir), local_files_only=True)
    model = MarianMTModel.from_pretrained(str(source_dir), local_files_only=True)
    model.to("cpu")
    model.eval()

    if tokenizer.pad_token_id != EXPECTED_PAD_TOKEN_ID:
        raise ReferenceFixtureError(
            f"tokenizer PAD drift: expected {EXPECTED_PAD_TOKEN_ID}, found {tokenizer.pad_token_id}"
        )
    if model.config.decoder_start_token_id != EXPECTED_DECODER_START_TOKEN_ID:
        raise ReferenceFixtureError(
            "model decoder-start drift: "
            f"expected {EXPECTED_DECODER_START_TOKEN_ID}, found {model.config.decoder_start_token_id}"
        )
    if model.config.eos_token_id != EXPECTED_EOS_TOKEN_ID:
        raise ReferenceFixtureError(
            f"model EOS drift: expected {EXPECTED_EOS_TOKEN_ID}, found {model.config.eos_token_id}"
        )

    fixture_samples: list[dict[str, Any]] = []
    with torch.inference_mode():
        for source_text in samples:
            encoded = tokenizer(
                source_text,
                return_tensors="pt",
                padding=False,
                truncation=True,
                max_length=maximum_source_tokens,
                add_special_tokens=True,
            )
            source_token_ids = [int(value) for value in encoded["input_ids"][0].tolist()]
            if not source_token_ids:
                raise ReferenceFixtureError(f"reference tokenizer emitted no source tokens for {source_text!r}")

            sequence = model.generate(
                input_ids=encoded["input_ids"],
                attention_mask=encoded["attention_mask"],
                num_beams=1,
                do_sample=False,
                max_new_tokens=maximum_target_tokens,
                decoder_start_token_id=EXPECTED_DECODER_START_TOKEN_ID,
                eos_token_id=EXPECTED_EOS_TOKEN_ID,
                pad_token_id=EXPECTED_PAD_TOKEN_ID,
                bad_words_ids=[[EXPECTED_PAD_TOKEN_ID]],
                forced_eos_token_id=EXPECTED_EOS_TOKEN_ID,
                renormalize_logits=True,
                use_cache=True,
            )[0]
            generated_token_ids = _strip_decoder_start([int(value) for value in sequence.tolist()])
            translated_text = tokenizer.decode(generated_token_ids, skip_special_tokens=True).strip()
            if not translated_text:
                raise ReferenceFixtureError(f"reference translation decoded empty for {source_text!r}")

            fixture_samples.append(
                {
                    "source_text": source_text,
                    "source_token_ids": source_token_ids,
                    "source_was_truncated": len(source_token_ids) >= maximum_source_tokens,
                    "generated_token_ids": generated_token_ids,
                    "translated_text": translated_text,
                }
            )

    return {
        "schema_version": 1,
        "purpose": "phrase-layer-marian-greedy-reference",
        "model_id": source["model_id"],
        "revision": source["revision"],
        "source_weight_sha256": source["weight_artifact"]["sha256"],
        "source_weight_size_bytes": source["weight_artifact"]["size_bytes"],
        "reference_runtime": "Transformers MarianMTModel fp32 CPU",
        "toolchain": toolchain,
        "network_policy": "offline-only",
        "generation": {
            "beam_width": 1,
            "do_sample": False,
            "maximum_source_tokens": maximum_source_tokens,
            "maximum_target_tokens": maximum_target_tokens,
            "decoder_start_token_id": EXPECTED_DECODER_START_TOKEN_ID,
            "pad_token_id": EXPECTED_PAD_TOKEN_ID,
            "eos_token_id": EXPECTED_EOS_TOKEN_ID,
            "banned_token_ids": [EXPECTED_PAD_TOKEN_ID],
            "force_eos_at_maximum_tokens": True,
            "renormalize_logits": True,
        },
        "samples": fixture_samples,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-dir", type=Path, required=True)
    parser.add_argument("--repository-root", type=Path, default=Path("."))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--maximum-source-tokens", type=int, default=DEFAULT_MAXIMUM_SOURCE_TOKENS)
    parser.add_argument("--maximum-target-tokens", type=int, default=DEFAULT_MAXIMUM_TARGET_TOKENS)
    parser.add_argument("--sample", action="append", dest="samples")
    args = parser.parse_args()

    repository_root = args.repository_root.resolve()
    source_dir = args.source_dir.resolve()
    samples = args.samples if args.samples else list(DEFAULT_SAMPLES)
    fixture = generate_fixture(
        source_dir,
        repository_root,
        samples,
        args.maximum_source_tokens,
        args.maximum_target_tokens,
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(fixture, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(
        json.dumps(
            {
                "status": "pass",
                "purpose": fixture["purpose"],
                "revision": fixture["revision"],
                "sample_count": len(fixture["samples"]),
                "output": str(args.output),
            },
            sort_keys=True,
        )
    )


if __name__ == "__main__":
    main()
