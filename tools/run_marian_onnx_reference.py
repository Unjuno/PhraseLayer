#!/usr/bin/env python3
"""Run the pinned Marian EN->JA model through independent PyTorch and ONNX Runtime greedy paths.

The caller supplies the exact local source snapshot plus the three ONNX graphs produced by
`export_marian_onnx.py`. This tool does not download anything. It uses the pinned Transformers tokenizer/model
as the source oracle, executes the reviewed no-post-process ONNX cache ABI independently, bans PAD exactly as
PhraseLayer Core does, and requires exact generated-token parity before emitting reference files for Unity.
"""

from __future__ import annotations

import argparse
import json
import math
import pathlib
from typing import Any, Dict, Iterable, List, Mapping, Sequence, Tuple

VOCAB_SIZE = 46276
EOS_TOKEN_ID = 0
DECODER_START_TOKEN_ID = 46275
PAD_TOKEN_ID = 46275
MAX_SOURCE_TOKENS = 128
MAX_TARGET_TOKENS = 128
DECODER_LAYERS = 6
SOURCE_TEXT = "I was tired, so I went home, and I fell asleep immediately."


class ReferenceError(RuntimeError):
    pass


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise ReferenceError(message)


def _session_names(session: Any, direction: str) -> List[str]:
    values = session.get_inputs() if direction == "inputs" else session.get_outputs()
    result = [item.name for item in values]
    _require(all(isinstance(name, str) and name for name in result), f"invalid {direction} metadata")
    return result


def _present(layer: int, kind: str, key_or_value: str) -> str:
    return f"present.{layer}.{kind}.{key_or_value}"


def _past(layer: int, kind: str, key_or_value: str) -> str:
    return f"past_key_values.{layer}.{kind}.{key_or_value}"


def _validate_names(encoder: Any, decoder: Any, cached: Any) -> Dict[str, List[str]]:
    names = {
        "encoder_inputs": _session_names(encoder, "inputs"),
        "encoder_outputs": _session_names(encoder, "outputs"),
        "decoder_inputs": _session_names(decoder, "inputs"),
        "decoder_outputs": _session_names(decoder, "outputs"),
        "cached_inputs": _session_names(cached, "inputs"),
        "cached_outputs": _session_names(cached, "outputs"),
    }
    _require(names["encoder_inputs"] == ["input_ids", "attention_mask"], "encoder input ABI drift")
    _require("last_hidden_state" in names["encoder_outputs"], "encoder output ABI drift")
    for required in ("input_ids", "encoder_hidden_states", "encoder_attention_mask"):
        _require(required in names["decoder_inputs"], f"decoder input missing {required}")
        _require(required in names["cached_inputs"], f"cached decoder input missing {required}")
    _require("logits" in names["decoder_outputs"], "decoder logits output missing")
    _require("logits" in names["cached_outputs"], "cached decoder logits output missing")
    for layer in range(DECODER_LAYERS):
        for kind in ("decoder", "encoder"):
            for key_or_value in ("key", "value"):
                _require(_present(layer, kind, key_or_value) in names["decoder_outputs"], "initial cache ABI drift")
                _require(_past(layer, kind, key_or_value) in names["cached_inputs"], "past cache ABI drift")
        for key_or_value in ("key", "value"):
            _require(_present(layer, "decoder", key_or_value) in names["cached_outputs"], "cached self-cache ABI drift")
    return names


def _last_logits(value: Any, np: Any) -> Any:
    array = np.asarray(value)
    _require(array.ndim == 3 and array.shape[0] == 1 and array.shape[1] > 0, f"invalid logits shape {array.shape}")
    _require(array.shape[2] == VOCAB_SIZE, f"logits vocabulary drift: {array.shape}")
    last = np.asarray(array[0, -1, :], dtype=np.float64)
    _require(bool(np.isfinite(last).all()), "decoder logits contain NaN/Inf")
    return last


def _choose(last: Any, np: Any) -> Tuple[int, float, float, float]:
    allowed = last.copy()
    allowed[PAD_TOKEN_ID] = -np.inf
    selected = int(np.argmax(allowed))
    selected_logit = float(allowed[selected])
    finite = allowed[np.isfinite(allowed)]
    _require(finite.size >= 2, "not enough finite logits for argmax comparison")
    runner_up = float(np.partition(finite, -2)[-2])
    margin = selected_logit - runner_up
    _require(math.isfinite(selected_logit) and math.isfinite(runner_up) and math.isfinite(margin), "invalid argmax margin")
    return selected, selected_logit, runner_up, margin


def encode_source(source_dir: pathlib.Path, text: str, np: Any) -> Tuple[Any, Any, Any]:
    try:
        from transformers import MarianTokenizer  # type: ignore
    except ImportError as exc:
        raise RuntimeError("Marian reference requires transformers") from exc
    tokenizer = MarianTokenizer.from_pretrained(str(source_dir), local_files_only=True)
    encoded = tokenizer(
        text,
        return_tensors="np",
        truncation=True,
        max_length=MAX_SOURCE_TOKENS,
        add_special_tokens=True,
    )
    input_ids = np.asarray(encoded["input_ids"], dtype=np.int64)
    attention = np.asarray(encoded["attention_mask"], dtype=np.int64)
    _require(input_ids.ndim == 2 and input_ids.shape[0] == 1 and input_ids.shape[1] > 0, "source tokenization failed")
    _require(input_ids.shape == attention.shape, "source attention-mask shape mismatch")
    return tokenizer, input_ids, attention


def run_onnx(bundle_dir: pathlib.Path, input_ids: Any, attention: Any) -> Dict[str, Any]:
    try:
        import numpy as np  # type: ignore
        import onnxruntime as ort  # type: ignore
    except ImportError as exc:
        raise RuntimeError("Marian ONNX reference requires numpy and onnxruntime") from exc

    providers = ["CPUExecutionProvider"]
    encoder = ort.InferenceSession(str(bundle_dir / "encoder_model.onnx"), providers=providers)
    decoder = ort.InferenceSession(str(bundle_dir / "decoder_model.onnx"), providers=providers)
    cached = ort.InferenceSession(str(bundle_dir / "decoder_with_past_model.onnx"), providers=providers)
    names = _validate_names(encoder, decoder, cached)

    encoder_hidden = encoder.run(
        ["last_hidden_state"],
        {"input_ids": input_ids, "attention_mask": attention},
    )[0]

    generated: List[int] = []
    steps: List[Dict[str, Any]] = []
    previous = DECODER_START_TOKEN_ID
    initial_cross: Dict[str, Any] = {}
    self_cache: Dict[str, Any] = {}
    terminated = False

    for step in range(MAX_TARGET_TOKENS):
        token = np.asarray([[previous]], dtype=np.int64)
        if step == 0:
            output_names = names["decoder_outputs"]
            values = decoder.run(
                output_names,
                {
                    "input_ids": token,
                    "encoder_hidden_states": encoder_hidden,
                    "encoder_attention_mask": attention,
                },
            )
            outputs = dict(zip(output_names, values))
            for layer in range(DECODER_LAYERS):
                for key_or_value in ("key", "value"):
                    self_cache[_past(layer, "decoder", key_or_value)] = outputs[_present(layer, "decoder", key_or_value)]
                    initial_cross[_past(layer, "encoder", key_or_value)] = outputs[_present(layer, "encoder", key_or_value)]
        else:
            feeds: Dict[str, Any] = {
                "input_ids": token,
                "encoder_hidden_states": encoder_hidden,
                "encoder_attention_mask": attention,
            }
            feeds.update(self_cache)
            feeds.update(initial_cross)
            output_names = names["cached_outputs"]
            values = cached.run(output_names, feeds)
            outputs = dict(zip(output_names, values))
            next_self: Dict[str, Any] = {}
            for layer in range(DECODER_LAYERS):
                for key_or_value in ("key", "value"):
                    next_self[_past(layer, "decoder", key_or_value)] = outputs[_present(layer, "decoder", key_or_value)]
            self_cache = next_self

        selected, selected_logit, runner_up, margin = _choose(_last_logits(outputs["logits"], np), np)
        steps.append({
            "step": step,
            "selected_token_id": selected,
            "selected_logit": selected_logit,
            "runner_up_logit": runner_up,
            "argmax_margin": margin,
        })
        generated.append(selected)
        if selected == EOS_TOKEN_ID:
            terminated = True
            break
        previous = selected

    _require(terminated, "ONNX greedy generation did not reach EOS")
    return {
        "token_ids": generated,
        "decoder_steps": len(steps),
        "terminated_by_eos": terminated,
        "steps": steps,
        "provider": providers[0],
    }


def run_pytorch(source_dir: pathlib.Path, input_ids: Any, attention: Any) -> List[int]:
    try:
        import torch  # type: ignore
        from transformers import MarianMTModel  # type: ignore
    except ImportError as exc:
        raise RuntimeError("Marian PyTorch reference requires torch and transformers") from exc
    model = MarianMTModel.from_pretrained(str(source_dir), local_files_only=True)
    model.eval()
    with torch.no_grad():
        output = model.generate(
            input_ids=torch.as_tensor(input_ids, dtype=torch.long),
            attention_mask=torch.as_tensor(attention, dtype=torch.long),
            max_new_tokens=MAX_TARGET_TOKENS,
            num_beams=1,
            do_sample=False,
            bad_words_ids=[[PAD_TOKEN_ID]],
            forced_eos_token_id=EOS_TOKEN_ID,
            renormalize_logits=True,
        )
    ids = [int(value) for value in output[0].tolist()]
    # Transformers returns decoder_start_token_id as the first sequence item; Core generation output starts after it.
    _require(ids and ids[0] == DECODER_START_TOKEN_ID, "PyTorch generation decoder-start token drift")
    return ids[1:]


def write_outputs(
    output_json: pathlib.Path,
    output_tokens: pathlib.Path,
    output_translation: pathlib.Path,
    source_text: str,
    source_ids: Sequence[int],
    onnx_report: Mapping[str, Any],
    pytorch_ids: Sequence[int],
    translation: str,
) -> Dict[str, Any]:
    token_ids = list(onnx_report["token_ids"])
    _require(token_ids == list(pytorch_ids), "ONNX token sequence differs from pinned PyTorch greedy reference")
    margins = [float(step["argmax_margin"]) for step in onnx_report["steps"]]
    report: Dict[str, Any] = {
        "schema_version": 1,
        "purpose": "marian-en-ja-greedy-unity-parity-reference",
        "source_text": source_text,
        "source_token_ids": list(source_ids),
        "token_ids": token_ids,
        "translation": translation,
        "terminated_by_eos": bool(onnx_report["terminated_by_eos"]),
        "decoder_steps": int(onnx_report["decoder_steps"]),
        "minimum_argmax_margin": min(margins),
        "steps": list(onnx_report["steps"]),
        "onnx_provider": onnx_report["provider"],
        "pytorch_exact_token_parity": True,
    }
    output_json.parent.mkdir(parents=True, exist_ok=True)
    output_tokens.parent.mkdir(parents=True, exist_ok=True)
    output_translation.parent.mkdir(parents=True, exist_ok=True)
    output_json.write_text(json.dumps(report, indent=2, ensure_ascii=False, sort_keys=True) + "\n", encoding="utf-8")
    output_tokens.write_text("".join(f"{value}\n" for value in token_ids), encoding="utf-8")
    output_translation.write_text(translation + "\n", encoding="utf-8")
    return report


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-dir", type=pathlib.Path, required=True)
    parser.add_argument("--bundle-dir", type=pathlib.Path, required=True)
    parser.add_argument("--source-text", default=SOURCE_TEXT)
    parser.add_argument("--output-json", type=pathlib.Path, required=True)
    parser.add_argument("--output-tokens", type=pathlib.Path, required=True)
    parser.add_argument("--output-translation", type=pathlib.Path, required=True)
    args = parser.parse_args()

    try:
        import numpy as np  # type: ignore
    except ImportError as exc:
        raise RuntimeError("Marian reference requires numpy") from exc

    tokenizer, input_ids, attention = encode_source(args.source_dir, args.source_text, np)
    onnx_report = run_onnx(args.bundle_dir, input_ids, attention)
    pytorch_ids = run_pytorch(args.source_dir, input_ids, attention)
    translation = tokenizer.decode(onnx_report["token_ids"], skip_special_tokens=True).strip()
    _require(translation, "Marian reference decoded an empty translation")
    report = write_outputs(
        args.output_json,
        args.output_tokens,
        args.output_translation,
        args.source_text,
        [int(value) for value in input_ids[0].tolist()],
        onnx_report,
        pytorch_ids,
        translation,
    )
    print(json.dumps(report, ensure_ascii=False, sort_keys=True))


if __name__ == "__main__":
    main()
