#!/usr/bin/env python3
"""One-shot English->Japanese local translation candidate comparison.

This is a host-side quality/size screening probe, not a Quest performance benchmark. It resolves each model's
full Hugging Face revision before loading, runs identical sentence and semantic-fragment cases at beam 1 and 4,
and uploads metadata/output only. Model weights remain in the ephemeral GitHub runner cache.
"""

from __future__ import annotations

import argparse
import gc
import json
import platform
import sys
import time
import traceback
from pathlib import Path
from typing import Any

CANDIDATES = [
    {
        "id": "Helsinki-NLP/opus-mt-en-jap",
        "expected_license": "apache-2.0",
        "policy": "redistribution-candidate",
    },
    {
        "id": "Helsinki-NLP/opus-tatoeba-en-ja",
        "expected_license": "apache-2.0",
        "policy": "redistribution-candidate",
    },
    {
        "id": "la-min/translate-en-ja",
        "expected_license": "apache-2.0",
        "policy": "redistribution-candidate",
    },
    {
        "id": "staka/fugumt-en-ja",
        "expected_license": "cc-by-sa-4.0",
        "policy": "quality-reference-license-review-required",
    },
]

SENTENCE_CASES = [
    ("I was tired, so I went home.", "疲れていたので、家に帰った。"),
    ("Please keep off the grass.", "芝生に入らないでください。"),
    ("I ran into an old friend at the station.", "駅で昔の友人に偶然会った。"),
    ("You are supposed to wear a helmet.", "ヘルメットを着用することになっています。"),
    ("In spite of the rain, we kept walking.", "雨にもかかわらず、私たちは歩き続けた。"),
    ("The meeting has been put off until Friday.", "会議は金曜日まで延期された。"),
    ("Turn left after the pharmacy.", "薬局を過ぎたら左に曲がってください。"),
    ("This device must remain unplugged during maintenance.", "メンテナンス中はこの機器の電源プラグを抜いたままにしてください。"),
    ("I didn't mean to wake you up.", "起こすつもりはなかった。"),
    ("Could you tell me where the nearest restroom is?", "一番近いトイレがどこか教えていただけますか。"),
]

FRAGMENT_CASES = [
    ("run into", "偶然会う"),
    ("be supposed to", "することになっている"),
    ("in spite of", "にもかかわらず"),
    ("put off", "延期する"),
    ("fell asleep immediately", "すぐに眠りに落ちた"),
]

BEAM_WIDTHS = [1, 4]
MAX_NEW_TOKENS = 64


def card_license(info: Any) -> str | None:
    card = getattr(info, "card_data", None)
    if card is None:
        return None
    if isinstance(card, dict):
        value = card.get("license")
    else:
        value = getattr(card, "license", None)
    return str(value).lower() if value else None


def weight_files(info: Any) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for sibling in getattr(info, "siblings", []) or []:
        name = str(getattr(sibling, "rfilename", ""))
        if not name.endswith((".bin", ".safetensors", ".h5")):
            continue
        result.append({
            "path": name,
            "size_bytes": getattr(sibling, "size", None),
        })
    return result


def generate_cases(model: Any, tokenizer: Any, cases: list[tuple[str, str]], beam_width: int) -> dict[str, Any]:
    import torch
    from sacrebleu.metrics import CHRF

    sources = [source for source, _ in cases]
    references = [reference for _, reference in cases]
    encoded = tokenizer(sources, return_tensors="pt", padding=True, truncation=True)
    started = time.perf_counter()
    with torch.inference_mode():
        generated = model.generate(
            **encoded,
            num_beams=beam_width,
            max_new_tokens=MAX_NEW_TOKENS,
            renormalize_logits=True,
            do_sample=False,
        )
    elapsed = time.perf_counter() - started
    hypotheses = tokenizer.batch_decode(generated, skip_special_tokens=True)
    metric = CHRF(word_order=0)
    score = metric.corpus_score(hypotheses, [references]).score
    rows = [
        {
            "source": source,
            "reference": reference,
            "translation": hypothesis,
        }
        for source, reference, hypothesis in zip(sources, references, hypotheses)
    ]
    return {
        "beam_width": beam_width,
        "chrf": round(float(score), 4),
        "host_generation_seconds": round(elapsed, 4),
        "cases": rows,
    }


def evaluate_candidate(candidate: dict[str, str]) -> dict[str, Any]:
    from huggingface_hub import model_info
    from transformers import AutoModelForSeq2SeqLM, AutoTokenizer

    model_id = candidate["id"]
    info = model_info(model_id, files_metadata=True)
    revision = str(info.sha)
    observed_license = card_license(info)
    result: dict[str, Any] = {
        "id": model_id,
        "revision": revision,
        "expected_license": candidate["expected_license"],
        "observed_license": observed_license,
        "policy": candidate["policy"],
        "weight_files": weight_files(info),
    }
    if observed_license != candidate["expected_license"]:
        raise RuntimeError(
            f"{model_id}: expected license {candidate['expected_license']} but observed {observed_license}"
        )

    tokenizer = AutoTokenizer.from_pretrained(model_id, revision=revision, trust_remote_code=False)
    model = AutoModelForSeq2SeqLM.from_pretrained(model_id, revision=revision, trust_remote_code=False)
    model.eval()
    result["architecture"] = type(model).__name__
    result["parameter_count"] = int(sum(parameter.numel() for parameter in model.parameters()))
    result["model_dtype"] = str(getattr(model, "dtype", "unknown"))
    result["config"] = {
        "d_model": getattr(model.config, "d_model", None),
        "encoder_layers": getattr(model.config, "encoder_layers", None),
        "decoder_layers": getattr(model.config, "decoder_layers", None),
        "vocab_size": getattr(model.config, "vocab_size", None),
        "decoder_start_token_id": getattr(model.config, "decoder_start_token_id", None),
        "eos_token_id": getattr(model.config, "eos_token_id", None),
        "pad_token_id": getattr(model.config, "pad_token_id", None),
    }
    result["beam_results"] = []
    for beam_width in BEAM_WIDTHS:
        result["beam_results"].append({
            "sentences": generate_cases(model, tokenizer, SENTENCE_CASES, beam_width),
            "fragments": generate_cases(model, tokenizer, FRAGMENT_CASES, beam_width),
        })

    del model, tokenizer
    gc.collect()
    return result


def write_report(path: Path, report: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()

    report: dict[str, Any] = {
        "schema_version": 1,
        "purpose": "host-side-translation-candidate-screening-not-quest-benchmark",
        "environment": {
            "python": sys.version,
            "platform": platform.platform(),
        },
        "sentence_case_count": len(SENTENCE_CASES),
        "fragment_case_count": len(FRAGMENT_CASES),
        "beam_widths": BEAM_WIDTHS,
        "candidates": [],
        "status": "started",
    }
    write_report(args.report, report)

    try:
        for candidate in CANDIDATES:
            try:
                evaluated = evaluate_candidate(candidate)
                evaluated["status"] = "pass"
                report["candidates"].append(evaluated)
            except Exception as exception:  # noqa: BLE001 - preserve per-model diagnosis
                report["candidates"].append({
                    "id": candidate["id"],
                    "status": "fail",
                    "error_type": type(exception).__name__,
                    "error": str(exception),
                    "traceback": traceback.format_exc(),
                })
            write_report(args.report, report)

        failures = [item for item in report["candidates"] if item.get("status") != "pass"]
        if failures:
            raise RuntimeError(f"{len(failures)} translation candidate(s) failed the comparison probe")

        report["status"] = "pass"
        write_report(args.report, report)
        return 0
    except Exception as exception:  # noqa: BLE001
        report["status"] = "fail"
        report["error_type"] = type(exception).__name__
        report["error"] = str(exception)
        report["traceback"] = traceback.format_exc()
        write_report(args.report, report)
        raise


if __name__ == "__main__":
    raise SystemExit(main())
