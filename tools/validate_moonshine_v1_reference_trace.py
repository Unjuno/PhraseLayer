#!/usr/bin/env python3
"""Validate the committed Moonshine v1 Beckett greedy reference evidence.

This validates only small, reviewable metadata. It cross-checks deployment graph identity against the
separate ONNX evidence file, tokenizer/fixture pins against PhraseLayer's fetch/runtime constants, and
internal consistency of the generated greedy trace. Optional --actual-trace compares a freshly generated
ONNX Runtime trace with the committed reference without weakening exact token parity.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import math
import pathlib
import re
from typing import Any, Mapping

ROOT = pathlib.Path(__file__).resolve().parents[1]
DEFAULT_REFERENCE = ROOT / "models/evidence/moonshine-v1-tiny.beckett.reference.json"
DEFAULT_GRAPH_EVIDENCE = ROOT / "models/evidence/moonshine-v1-tiny.35b4aae79f7d598a4d36d5252ec26ad642faab60.onnx.json"
EXPECTED_GRAPH_MODEL = "moonshine-ai/moonshine"
EXPECTED_GRAPH_REVISION = "35b4aae79f7d598a4d36d5252ec26ad642faab60"
EXPECTED_TOKENIZER_MODEL = "moonshine-ai/moonshine-tiny"
EXPECTED_TOKENIZER_REVISION = "390624ed33d594443aa4aa221f5b9f283b545b5a"
EXPECTED_VOCABULARY_SIZE = 32768
EXPECTED_DECODER_START = 1
EXPECTED_EOS = 2
EXPECTED_MAX_GENERATION = 194


class ReferenceEvidenceError(ValueError):
    pass


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise ReferenceEvidenceError(message)


def _load_json(path: pathlib.Path) -> Mapping[str, Any]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ReferenceEvidenceError(f"failed to parse {path}: {exc}") from exc
    _require(isinstance(payload, dict), f"{path} must contain a JSON object")
    return payload


def _load_fixture_module() -> Any:
    path = ROOT / "tools/fetch_moonshine_beckett_fixture.py"
    spec = importlib.util.spec_from_file_location("phrase_layer_beckett_fixture_contract", path)
    _require(spec is not None and spec.loader is not None, "failed to load Beckett fixture contract")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _normalize_words(value: str) -> str:
    return " ".join(re.findall(r"[a-z0-9]+", value.lower()))


def validate_reference(
    reference: Mapping[str, Any],
    graph_evidence: Mapping[str, Any],
    actual_trace: Mapping[str, Any] | None = None,
) -> Mapping[str, Any]:
    fixture_contract = _load_fixture_module()
    _require(reference.get("schema_version") == 1, "reference schema_version drift")
    _require(reference.get("purpose") == "moonshine-v1-greedy-reference-parity", "reference purpose drift")

    deployment = reference.get("deployment")
    _require(isinstance(deployment, dict), "reference deployment block missing")
    _require(deployment.get("model_id") == EXPECTED_GRAPH_MODEL, "reference deployment model_id drift")
    _require(deployment.get("revision") == EXPECTED_GRAPH_REVISION, "reference deployment revision drift")
    _require(deployment.get("bundle_kind") == "moonshine-v1-four-graph", "reference deployment bundle drift")
    _require(deployment.get("binding") == "positional", "reference deployment binding drift")

    _require(graph_evidence.get("schema_version") == 1, "graph evidence schema drift")
    _require(graph_evidence.get("model_id") == deployment.get("model_id"), "reference/graph model_id mismatch")
    _require(graph_evidence.get("revision") == deployment.get("revision"), "reference/graph revision mismatch")
    _require(graph_evidence.get("bundle_kind") == deployment.get("bundle_kind"), "reference/graph bundle mismatch")
    _require(graph_evidence.get("binding") == deployment.get("binding"), "reference/graph binding mismatch")
    _require(graph_evidence.get("vocabulary_size") == EXPECTED_VOCABULARY_SIZE, "graph evidence vocabulary drift")

    tokenizer = reference.get("tokenizer")
    _require(isinstance(tokenizer, dict), "reference tokenizer block missing")
    _require(tokenizer.get("model_id") == EXPECTED_TOKENIZER_MODEL, "reference tokenizer model_id drift")
    _require(tokenizer.get("revision") == EXPECTED_TOKENIZER_REVISION, "reference tokenizer revision drift")
    _require(tokenizer.get("vocabulary_size") == EXPECTED_VOCABULARY_SIZE, "reference tokenizer vocabulary drift")
    _require(tokenizer.get("decoder_start_token_id") == EXPECTED_DECODER_START, "decoder start token drift")
    _require(tokenizer.get("eos_token_id") == EXPECTED_EOS, "EOS token drift")

    fixture = reference.get("fixture")
    _require(isinstance(fixture, dict), "reference fixture block missing")
    expected_fixture_fields = {
        "repository": fixture_contract.UPSTREAM_REPOSITORY,
        "revision": fixture_contract.UPSTREAM_REVISION,
        "path": fixture_contract.UPSTREAM_PATH,
        "git_blob_sha1": fixture_contract.EXPECTED_GIT_BLOB_SHA1,
        "size_bytes": fixture_contract.EXPECTED_SIZE_BYTES,
        "canonical_spoken_text": fixture_contract.EXPECTED_SPOKEN_TEXT,
    }
    for field, expected in expected_fixture_fields.items():
        _require(fixture.get(field) == expected, f"reference fixture {field} drift")
    sha256 = fixture.get("sha256")
    _require(isinstance(sha256, str) and re.fullmatch(r"[0-9a-f]{64}", sha256) is not None, "fixture sha256 invalid")

    runtime = reference.get("reference_runtime")
    _require(isinstance(runtime, dict), "reference runtime block missing")
    _require(runtime.get("engine") == "onnxruntime", "reference runtime engine drift")
    _require(runtime.get("provider") == "CPUExecutionProvider", "reference runtime provider drift")
    artifact_digest = runtime.get("artifact_digest")
    _require(
        isinstance(artifact_digest, str) and re.fullmatch(r"sha256:[0-9a-f]{64}", artifact_digest) is not None,
        "reference runtime artifact digest invalid",
    )

    generation = reference.get("generation")
    _require(isinstance(generation, dict), "reference generation block missing")
    _require(generation.get("strategy") == "greedy", "reference generation strategy drift")
    _require(generation.get("maximum_generation_length") == EXPECTED_MAX_GENERATION, "generation maximum drift")
    _require(generation.get("terminated_by_eos") is True, "reference must terminate by EOS")

    token_ids = generation.get("token_ids")
    _require(isinstance(token_ids, list) and token_ids, "reference token_ids missing")
    for index, token_id in enumerate(token_ids):
        _require(isinstance(token_id, int) and not isinstance(token_id, bool), f"token_ids[{index}] must be an integer")
        _require(0 <= token_id < EXPECTED_VOCABULARY_SIZE, f"token_ids[{index}] out of range")
        _require(token_id != EXPECTED_EOS, "emitted token_ids must not include EOS")
    _require(generation.get("emitted_token_count") == len(token_ids), "emitted token count mismatch")
    _require(generation.get("decoder_steps") == len(token_ids) + 1, "EOS-terminated decoder step count mismatch")

    steps = generation.get("steps")
    _require(isinstance(steps, list) and len(steps) == len(token_ids) + 1, "reference step list length mismatch")
    margins = []
    for index, step in enumerate(steps):
        _require(isinstance(step, dict), f"reference step {index} must be an object")
        _require(step.get("step") == index, f"reference step index drift at {index}")
        expected_token = token_ids[index] if index < len(token_ids) else EXPECTED_EOS
        _require(step.get("selected_token_id") == expected_token, f"reference selected token drift at step {index}")
        selected = step.get("selected_logit")
        runner_up = step.get("runner_up_logit")
        margin = step.get("argmax_margin")
        _require(isinstance(selected, (int, float)) and math.isfinite(float(selected)), f"selected logit invalid at step {index}")
        _require(isinstance(runner_up, (int, float)) and math.isfinite(float(runner_up)), f"runner-up logit invalid at step {index}")
        _require(isinstance(margin, (int, float)) and math.isfinite(float(margin)), f"argmax margin invalid at step {index}")
        _require(float(margin) > 0.0, f"argmax margin must be positive at step {index}")
        _require(abs((float(selected) - float(runner_up)) - float(margin)) <= 1e-9, f"argmax margin arithmetic drift at step {index}")
        margins.append(float(margin))

    minimum_margin = min(margins)
    declared_minimum = generation.get("minimum_argmax_margin")
    _require(isinstance(declared_minimum, (int, float)), "minimum_argmax_margin missing")
    _require(abs(float(declared_minimum) - minimum_margin) <= 1e-9, "minimum_argmax_margin mismatch")

    transcript = generation.get("transcript")
    _require(isinstance(transcript, str) and transcript.strip(), "reference transcript missing")
    _require(
        _normalize_words(transcript) == _normalize_words(fixture_contract.EXPECTED_SPOKEN_TEXT),
        "reference transcript no longer matches canonical fixture speech",
    )

    if actual_trace is not None:
        _require(actual_trace.get("schema_version") == 1, "actual trace schema drift")
        _require(actual_trace.get("audio_size_bytes") == fixture.get("size_bytes"), "actual trace audio size mismatch")
        _require(actual_trace.get("audio_sha256") == fixture.get("sha256"), "actual trace audio sha256 mismatch")
        _require(actual_trace.get("binding") == deployment.get("binding"), "actual trace binding mismatch")
        _require(actual_trace.get("provider") == runtime.get("provider"), "actual trace provider mismatch")
        _require(actual_trace.get("token_ids") == token_ids, "actual trace token sequence differs from committed reference")
        _require(actual_trace.get("terminated_by_eos") is True, "actual trace did not terminate by EOS")
        _require(actual_trace.get("decoder_steps") == generation.get("decoder_steps"), "actual trace decoder step count mismatch")
        _require(actual_trace.get("transcript") == transcript, "actual trace transcript differs from committed reference")

    return {
        "status": "validated",
        "emitted_token_count": len(token_ids),
        "decoder_steps": generation.get("decoder_steps"),
        "minimum_argmax_margin": minimum_margin,
        "transcript": transcript,
        "actual_trace_compared": actual_trace is not None,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", type=pathlib.Path, default=DEFAULT_REFERENCE)
    parser.add_argument("--graph-evidence", type=pathlib.Path, default=DEFAULT_GRAPH_EVIDENCE)
    parser.add_argument("--actual-trace", type=pathlib.Path)
    args = parser.parse_args()
    reference = _load_json(args.reference)
    graph_evidence = _load_json(args.graph_evidence)
    actual = _load_json(args.actual_trace) if args.actual_trace is not None else None
    print(json.dumps(validate_reference(reference, graph_evidence, actual), sort_keys=True))


if __name__ == "__main__":
    main()
