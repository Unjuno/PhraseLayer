#!/usr/bin/env python3
"""Run deterministic greedy Moonshine v1 inference with ONNX Runtime.

This is an independent parity oracle for the Unity Inference Engine backend. It binds the reviewed
four-graph ABI positionally, keeps decoder cache in ONNX Runtime, records every selected token and
argmax margin, and optionally decodes the final token sequence with the pinned Hugging Face tokenizer.
No PhraseLayer C# runtime code is executed by this tool.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import pathlib
import wave
from typing import Any, Dict, Iterable, List, Sequence

PREPROCESS = "preprocess.onnx"
ENCODER = "encode.onnx"
UNCACHED = "uncached_decode.onnx"
CACHED = "cached_decode.onnx"
CACHE_STATE_COUNT = 24
SAMPLE_RATE = 16000
VOCAB_SIZE = 32768
DECODER_START_TOKEN_ID = 1
EOS_TOKEN_ID = 2
MAXIMUM_GENERATION_LENGTH = 194


class ReferenceError(ValueError):
    pass


class DecodeStep:
    def __init__(
        self,
        step: int,
        selected_token_id: int,
        selected_logit: float,
        runner_up_logit: float,
        argmax_margin: float,
    ) -> None:
        self.step = step
        self.selected_token_id = selected_token_id
        self.selected_logit = selected_logit
        self.runner_up_logit = runner_up_logit
        self.argmax_margin = argmax_margin


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise ReferenceError(message)


def _names(items: Iterable[Any]) -> List[str]:
    result = []
    for item in items:
        name = getattr(item, "name", None)
        _require(isinstance(name, str) and name, "ONNX Runtime tensor metadata is missing a name")
        result.append(name)
    return result


def validate_positional_session_abi(
    preprocess: Any,
    encoder: Any,
    uncached: Any,
    cached: Any,
) -> Dict[str, List[str]]:
    names = {
        "preprocess_inputs": _names(preprocess.get_inputs()),
        "preprocess_outputs": _names(preprocess.get_outputs()),
        "encoder_inputs": _names(encoder.get_inputs()),
        "encoder_outputs": _names(encoder.get_outputs()),
        "uncached_inputs": _names(uncached.get_inputs()),
        "uncached_outputs": _names(uncached.get_outputs()),
        "cached_inputs": _names(cached.get_inputs()),
        "cached_outputs": _names(cached.get_outputs()),
    }
    expected_counts = {
        "preprocess_inputs": 1,
        "preprocess_outputs": 1,
        "encoder_inputs": 2,
        "encoder_outputs": 1,
        "uncached_inputs": 3,
        "uncached_outputs": 1 + CACHE_STATE_COUNT,
        "cached_inputs": 3 + CACHE_STATE_COUNT,
        "cached_outputs": 1 + CACHE_STATE_COUNT,
    }
    for key, expected in expected_counts.items():
        actual = len(names[key])
        _require(actual == expected, f"{key} expected {expected} tensors but received {actual}")
    return names


def read_pcm16_wave(path: pathlib.Path, np: Any) -> Any:
    try:
        with wave.open(str(path), "rb") as handle:
            channels = handle.getnchannels()
            sample_width = handle.getsampwidth()
            sample_rate = handle.getframerate()
            frames = handle.getnframes()
            compression = handle.getcomptype()
            data = handle.readframes(frames)
    except (OSError, wave.Error) as exc:
        raise ReferenceError(f"failed to decode WAV fixture: {exc}") from exc

    _require(compression == "NONE", "reference fixture must be uncompressed PCM WAV")
    _require(sample_width == 2, "reference fixture must use PCM16 samples")
    _require(channels > 0, "reference fixture must have at least one channel")
    _require(sample_rate == SAMPLE_RATE, f"reference fixture must be {SAMPLE_RATE} Hz")

    samples = np.frombuffer(data, dtype="<i2").astype(np.float32)
    _require(samples.size % channels == 0, "WAV sample count is not divisible by channel count")
    samples = samples.reshape((-1, channels)).mean(axis=1)
    return samples / np.float32(32768.0)


def _validate_logits(logits: Any, np: Any) -> Any:
    logits = np.asarray(logits)
    _require(logits.ndim == 3, f"decoder logits must be rank 3, received shape {tuple(logits.shape)}")
    _require(logits.shape[0] == 1, "decoder logits batch must be 1")
    _require(logits.shape[1] > 0, "decoder logits sequence length must be positive")
    _require(logits.shape[2] == VOCAB_SIZE, f"decoder logits vocab must be {VOCAB_SIZE}")
    last = np.asarray(logits[0, -1, :], dtype=np.float64)
    _require(bool(np.isfinite(last).all()), "decoder logits contain NaN or infinity")
    return last


def _select_step(step: int, last_logits: Any, np: Any) -> DecodeStep:
    selected = int(np.argmax(last_logits))
    selected_logit = float(last_logits[selected])
    if last_logits.size > 1:
        runner_up = float(np.partition(last_logits, -2)[-2])
    else:
        runner_up = float("-inf")
    margin = selected_logit - runner_up
    _require(math.isfinite(selected_logit), "selected logit is not finite")
    _require(math.isfinite(margin) or math.isinf(margin), "argmax margin is invalid")
    return DecodeStep(step, selected, selected_logit, runner_up, margin)


def run_greedy(
    bundle_dir: pathlib.Path,
    audio: Any,
    maximum_generation_length: int = MAXIMUM_GENERATION_LENGTH,
) -> Dict[str, Any]:
    _require(0 < maximum_generation_length <= MAXIMUM_GENERATION_LENGTH, "invalid generation limit")
    try:
        import numpy as np  # type: ignore
        import onnxruntime as ort  # type: ignore
    except ImportError as exc:
        raise RuntimeError("Moonshine v1 reference inference requires numpy and onnxruntime") from exc

    providers = ["CPUExecutionProvider"]
    sessions = {
        "preprocess": ort.InferenceSession(str(bundle_dir / PREPROCESS), providers=providers),
        "encoder": ort.InferenceSession(str(bundle_dir / ENCODER), providers=providers),
        "uncached": ort.InferenceSession(str(bundle_dir / UNCACHED), providers=providers),
        "cached": ort.InferenceSession(str(bundle_dir / CACHED), providers=providers),
    }
    names = validate_positional_session_abi(
        sessions["preprocess"], sessions["encoder"], sessions["uncached"], sessions["cached"]
    )

    waveform = np.asarray(audio, dtype=np.float32)
    _require(waveform.ndim == 1 and waveform.size > 0, "reference audio must be a non-empty mono vector")
    features = sessions["preprocess"].run(
        None,
        {names["preprocess_inputs"][0]: waveform.reshape((1, -1))},
    )[0]
    features = np.asarray(features, dtype=np.float32)
    _require(features.ndim == 3 and features.shape[0] == 1 and features.shape[2] == 288, "preprocess feature shape drift")

    feature_length = np.asarray([features.shape[1]], dtype=np.int32)
    encoder_output = sessions["encoder"].run(
        None,
        {
            names["encoder_inputs"][0]: features,
            names["encoder_inputs"][1]: feature_length,
        },
    )[0]
    encoder_output = np.asarray(encoder_output, dtype=np.float32)
    _require(
        encoder_output.ndim == 3 and encoder_output.shape[0] == 1 and encoder_output.shape[2] == 288,
        "encoder output shape drift",
    )

    generated: List[int] = []
    steps: List[DecodeStep] = []
    previous_token = DECODER_START_TOKEN_ID
    token_length = 1
    cache: Sequence[Any] | None = None
    terminated_by_eos = False

    for step_index in range(maximum_generation_length):
        token = np.asarray([[previous_token]], dtype=np.int32)
        length = np.asarray([token_length], dtype=np.int32)
        if cache is None:
            outputs = sessions["uncached"].run(
                None,
                {
                    names["uncached_inputs"][0]: token,
                    names["uncached_inputs"][1]: encoder_output,
                    names["uncached_inputs"][2]: length,
                },
            )
        else:
            feeds = {
                names["cached_inputs"][0]: token,
                names["cached_inputs"][1]: encoder_output,
                names["cached_inputs"][2]: length,
            }
            for index, state in enumerate(cache):
                feeds[names["cached_inputs"][3 + index]] = state
            outputs = sessions["cached"].run(None, feeds)

        _require(len(outputs) == 1 + CACHE_STATE_COUNT, "decoder output count drift")
        last_logits = _validate_logits(outputs[0], np)
        selected = _select_step(step_index, last_logits, np)
        steps.append(selected)
        cache = tuple(outputs[1:])
        _require(len(cache) == CACHE_STATE_COUNT, "decoder cache count drift")

        if selected.selected_token_id == EOS_TOKEN_ID:
            terminated_by_eos = True
            break
        generated.append(selected.selected_token_id)
        previous_token = selected.selected_token_id
        token_length += 1

    return {
        "token_ids": generated,
        "terminated_by_eos": terminated_by_eos,
        "decoder_steps": len(steps),
        "steps": [
            {
                "step": item.step,
                "selected_token_id": item.selected_token_id,
                "selected_logit": item.selected_logit,
                "runner_up_logit": item.runner_up_logit,
                "argmax_margin": item.argmax_margin,
            }
            for item in steps
        ],
        "binding": "positional",
        "provider": providers[0],
    }


def decode_tokens(tokenizer_path: pathlib.Path, token_ids: Sequence[int]) -> str:
    try:
        from tokenizers import Tokenizer  # type: ignore
    except ImportError as exc:
        raise RuntimeError("Transcript decoding requires the optional tokenizers package") from exc
    tokenizer = Tokenizer.from_file(str(tokenizer_path))
    return tokenizer.decode(list(token_ids), skip_special_tokens=True).strip()


def write_outputs(
    report: Dict[str, Any],
    wav_path: pathlib.Path,
    output_json: pathlib.Path,
    output_tokens: pathlib.Path,
    output_transcript: pathlib.Path,
    transcript: str,
) -> Dict[str, Any]:
    wav_bytes = wav_path.read_bytes()
    enriched = dict(report)
    enriched.update(
        {
            "schema_version": 1,
            "audio_file": wav_path.name,
            "audio_size_bytes": len(wav_bytes),
            "audio_sha256": hashlib.sha256(wav_bytes).hexdigest(),
            "transcript": transcript,
        }
    )
    output_json.parent.mkdir(parents=True, exist_ok=True)
    output_tokens.parent.mkdir(parents=True, exist_ok=True)
    output_transcript.parent.mkdir(parents=True, exist_ok=True)
    output_json.write_text(json.dumps(enriched, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    output_tokens.write_text("".join(f"{token_id}\n" for token_id in enriched["token_ids"]), encoding="utf-8")
    output_transcript.write_text(transcript + "\n", encoding="utf-8")
    return enriched


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle-dir", type=pathlib.Path, required=True)
    parser.add_argument("--wav", type=pathlib.Path, required=True)
    parser.add_argument("--tokenizer", type=pathlib.Path, required=True)
    parser.add_argument("--output-json", type=pathlib.Path, required=True)
    parser.add_argument("--output-tokens", type=pathlib.Path, required=True)
    parser.add_argument("--output-transcript", type=pathlib.Path, required=True)
    parser.add_argument("--maximum-generation-length", type=int, default=MAXIMUM_GENERATION_LENGTH)
    args = parser.parse_args()

    try:
        import numpy as np  # type: ignore
    except ImportError as exc:
        raise RuntimeError("Moonshine v1 reference inference requires numpy") from exc

    audio = read_pcm16_wave(args.wav, np)
    report = run_greedy(args.bundle_dir, audio, args.maximum_generation_length)
    transcript = decode_tokens(args.tokenizer, report["token_ids"])
    enriched = write_outputs(
        report,
        args.wav,
        args.output_json,
        args.output_tokens,
        args.output_transcript,
        transcript,
    )
    print(json.dumps(enriched, sort_keys=True))


if __name__ == "__main__":
    main()
