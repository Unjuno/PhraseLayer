#!/usr/bin/env python3
"""Validate committed Moonshine v1 Beckett greedy-reference evidence.

This keeps the small committed reference evidence tied to the reviewed deployment graph evidence,
the pinned speech fixture, and the deterministic greedy decoding contract. Optionally compare a
fresh ONNX Runtime trace against the committed token/transcript result without requiring exact
floating-point logit equality across hosts.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import re
from typing import Any, Mapping, Sequence

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEFAULT_REFERENCE = ROOT / "models/evidence/moonshine-v1-tiny.beckett.reference.json"
EXPECTED_GRAPH = ROOT / "models/evidence/moonshine-v1-tiny.35b4aae79f7d598a4d36d5252ec26ad642faab60.onnx.json"
EXPECTED_DEPLOYMENT_REVISION = "35b4aae79f7d598a4d36d5252ec26ad642faab60"
EXPECTED_TOKENIZER_REVISION = "390624ed33d594443aa4aa221f5b9f283b545b5a"
EXPECTED_FIXTURE_REVISION = "49bc3af5bb0d767d5156fb579fa5f9853b559bf3"
EXPECTED_FIXTURE_PATH = "android/java/androidTest/assets/beckett.wav"
EXPECTED_FIXTURE_SIZE = 318978
EXPECTED_FIXTURE_SHA256 = "e5a26b3d29e5b0bc8dbd72c11d16a3808a4ec0ac45b21f12a27d2a2fe5c5ae61"
EXPECTED_VOCABULARY_SIZE = 32768
EXPECTED_DECODER_START = 1
EXPECTED_EOS = 2
EXPECTED_MAX_GENERATION = 194
MINIMUM_SAFE_ARGMAX_MARGIN = 0.1


class EvidenceError(ValueError):
    pass


def _load(path: pathlib.Path) -> Mapping[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise EvidenceError(f"failed to parse {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise EvidenceError(f"{path} must contain a JSON object")
    return value


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise EvidenceError(message)


def _normalize_text(value: str) -> str:
    return " ".join(re.findall(r"[a-z0-9]+", value.lower()))


def _integer_list(value: Any, label: str) -> list[int]:
    _require(isinstance(value, list) and value, f"{label} must be a non-empty list")
    result: list[int] = []
    for index, item in enumerate(value):
        _require(isinstance(item, int) and not isinstance(item, bool), f"{label}[{index}] must be an integer")
        _require(0 <= item < EXPECTED_VOCABULARY_SIZE, f"{label}[{index}] is outside the vocabulary")
        result.append(item)
    return result


def validate(reference: Mapping[str, Any], graph: Mapping[str, Any], runtime_trace: Mapping[str, Any] | None = None) -> dict[str, Any]:
    _require(reference.get("schema_version") == 1, "reference evidence schema drift")
    _require(reference.get("purpose") == "moonshine-v1-greedy-reference-parity", "reference evidence purpose drift")

    deployment = reference.get("deployment")
    _require(isinstance(deployment, dict), "reference deployment section missing")
    _require(deployment.get("revision") == EXPECTED_DEPLOYMENT_REVISION, "reference deployment revision drift")
    _require(deployment.get("bundle_kind") == "moonshine-v1-four-graph", "reference deployment bundle drift")
    _require(deployment.get("binding") == "positional", "reference deployment binding drift")
    _require(deployment.get("graph_evidence") == str(EXPECTED_GRAPH.relative_to(ROOT)).replace("\\", "/"), "reference graph evidence path drift")

    _require(graph.get("schema_version") == 1, "graph evidence schema drift")
    _require(graph.get("revision") == EXPECTED_DEPLOYMENT_REVISION, "graph evidence revision drift")
    _require(graph.get("bundle_kind") == deployment.get("bundle_kind"), "reference/graph bundle mismatch")
    _require(graph.get("binding") == deployment.get("binding"), "reference/graph binding mismatch")
    _require(graph.get("vocabulary_size") == EXPECTED_VOCABULARY_SIZE, "graph vocabulary drift")
    _require(graph.get("reference_parity_evidence") == str(DEFAULT_REFERENCE.relative_to(ROOT)).replace("\\", "/"), "graph/reference backlink drift")

    tokenizer = reference.get("tokenizer")
    _require(isinstance(tokenizer, dict), "reference tokenizer section missing")
    _require(tokenizer.get("revision") == EXPECTED_TOKENIZER_REVISION, "reference tokenizer revision drift")
    _require(tokenizer.get("vocabulary_size") == EXPECTED_VOCABULARY_SIZE, "reference tokenizer vocabulary drift")
    _require(tokenizer.get("decoder_start_token_id") == EXPECTED_DECODER_START, "reference decoder-start drift")
    _require(tokenizer.get("eos_token_id") == EXPECTED_EOS, "reference EOS drift")

    fixture = reference.get("fixture")
    _require(isinstance(fixture, dict), "reference fixture section missing")
    _require(fixture.get("revision") == EXPECTED_FIXTURE_REVISION, "fixture revision drift")
    _require(fixture.get("path") == EXPECTED_FIXTURE_PATH, "fixture path drift")
    _require(fixture.get("size_bytes") == EXPECTED_FIXTURE_SIZE, "fixture size drift")
    _require(fixture.get("sha256") == EXPECTED_FIXTURE_SHA256, "fixture SHA-256 drift")
    canonical = fixture.get("canonical_spoken_text")
    _require(isinstance(canonical, str) and canonical.strip(), "fixture canonical text missing")

    runtime = reference.get("reference_runtime")
    _require(isinstance(runtime, dict), "reference runtime section missing")
    _require(runtime.get("engine") == "onnxruntime", "reference engine drift")
    _require(runtime.get("provider") == "CPUExecutionProvider", "reference provider drift")

    generation = reference.get("generation")
    _require(isinstance(generation, dict), "reference generation section missing")
    _require(generation.get("strategy") == "greedy", "reference generation strategy drift")
    _require(generation.get("maximum_generation_length") == EXPECTED_MAX_GENERATION, "reference generation limit drift")
    _require(generation.get("terminated_by_eos") is True, "reference did not terminate by EOS")
    token_ids = _integer_list(generation.get("token_ids"), "reference token_ids")
    _require(generation.get("emitted_token_count") == len(token_ids), "reference emitted token count drift")
    decoder_steps = generation.get("decoder_steps")
    _require(decoder_steps == len(token_ids) + 1, "reference decoder step count must include the EOS step")

    transcript = generation.get("transcript")
    _require(isinstance(transcript, str) and transcript.strip(), "reference transcript missing")
    _require(_normalize_text(transcript) == _normalize_text(canonical), "reference transcript no longer matches canonical speech")

    steps = generation.get("steps")
    _require(isinstance(steps, list) and len(steps) == decoder_steps, "reference step evidence count drift")
    selected_ids: list[int] = []
    margins: list[float] = []
    for index, step in enumerate(steps):
        _require(isinstance(step, dict), f"reference step {index} is invalid")
        _require(step.get("step") == index, f"reference step index drift at {index}")
        token = step.get("selected_token_id")
        margin = step.get("argmax_margin")
        _require(isinstance(token, int) and not isinstance(token, bool), f"reference step {index} token is invalid")
        _require(isinstance(margin, (int, float)) and not isinstance(margin, bool), f"reference step {index} margin is invalid")
        _require(float(margin) >= MINIMUM_SAFE_ARGMAX_MARGIN, f"reference step {index} argmax margin is too fragile: {margin}")
        selected_ids.append(token)
        margins.append(float(margin))
    _require(selected_ids[:-1] == token_ids, "reference step tokens do not match emitted token_ids")
    _require(selected_ids[-1] == EXPECTED_EOS, "reference final step is not EOS")
    minimum_margin = min(margins)
    stored_minimum = generation.get("minimum_argmax_margin")
    _require(isinstance(stored_minimum, (int, float)) and abs(float(stored_minimum) - minimum_margin) < 1e-9, "reference minimum argmax margin drift")

    if runtime_trace is not None:
        live_tokens = _integer_list(runtime_trace.get("token_ids"), "runtime token_ids")
        _require(live_tokens == token_ids, "fresh ONNX Runtime token IDs differ from committed reference")
        _require(runtime_trace.get("terminated_by_eos") is True, "fresh ONNX Runtime trace did not terminate by EOS")
        _require(runtime_trace.get("decoder_steps") == decoder_steps, "fresh ONNX Runtime decoder step count differs")
        _require(runtime_trace.get("audio_size_bytes") == EXPECTED_FIXTURE_SIZE, "fresh fixture size differs")
        _require(runtime_trace.get("audio_sha256") == EXPECTED_FIXTURE_SHA256, "fresh fixture SHA-256 differs")
        live_transcript = runtime_trace.get("transcript")
        _require(isinstance(live_transcript, str), "fresh ONNX Runtime transcript missing")
        _require(live_transcript == transcript, "fresh ONNX Runtime transcript differs from committed reference")
        live_steps = runtime_trace.get("steps")
        _require(isinstance(live_steps, list) and len(live_steps) == decoder_steps, "fresh ONNX Runtime step evidence count differs")
        for index, step in enumerate(live_steps):
            _require(isinstance(step, dict), f"fresh step {index} is invalid")
            _require(step.get("selected_token_id") == selected_ids[index], f"fresh selected token differs at step {index}")
            margin = step.get("argmax_margin")
            _require(isinstance(margin, (int, float)) and float(margin) >= MINIMUM_SAFE_ARGMAX_MARGIN, f"fresh argmax margin is too fragile at step {index}: {margin}")

    return {
        "schema_version": 1,
        "status": "pass",
        "emitted_token_count": len(token_ids),
        "decoder_steps": decoder_steps,
        "minimum_argmax_margin": minimum_margin,
        "runtime_trace_compared": runtime_trace is not None,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", type=pathlib.Path, default=DEFAULT_REFERENCE)
    parser.add_argument("--graph-evidence", type=pathlib.Path, default=EXPECTED_GRAPH)
    parser.add_argument("--runtime-trace", type=pathlib.Path)
    args = parser.parse_args()
    reference = _load(args.reference)
    graph = _load(args.graph_evidence)
    runtime_trace = _load(args.runtime_trace) if args.runtime_trace is not None else None
    print(json.dumps(validate(reference, graph, runtime_trace), sort_keys=True))


if __name__ == "__main__":
    main()
