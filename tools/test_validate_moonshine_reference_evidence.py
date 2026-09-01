#!/usr/bin/env python3
from __future__ import annotations

import copy
import importlib.util
import json
import pathlib
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "tools/validate_moonshine_reference_evidence.py"
REFERENCE = ROOT / "models/evidence/moonshine-v1-tiny.beckett.reference.json"
GRAPH = ROOT / "models/evidence/moonshine-v1-tiny.35b4aae79f7d598a4d36d5252ec26ad642faab60.onnx.json"

spec = importlib.util.spec_from_file_location("moonshine_reference_validator", SCRIPT)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)


def load(path: pathlib.Path):
    return json.loads(path.read_text(encoding="utf-8"))


def expect_failure(reference, graph, contains: str, runtime=None):
    try:
        module.validate(reference, graph, runtime)
    except module.EvidenceError as exc:
        assert contains in str(exc), (contains, str(exc))
    else:
        raise AssertionError("expected EvidenceError")


def main() -> None:
    reference = load(REFERENCE)
    graph = load(GRAPH)
    report = module.validate(reference, graph)
    assert report["status"] == "pass"
    assert report["emitted_token_count"] == 16
    assert report["decoder_steps"] == 17
    assert report["runtime_trace_compared"] is False

    runtime = {
        "token_ids": list(reference["generation"]["token_ids"]),
        "terminated_by_eos": True,
        "decoder_steps": reference["generation"]["decoder_steps"],
        "audio_size_bytes": reference["fixture"]["size_bytes"],
        "audio_sha256": reference["fixture"]["sha256"],
        "transcript": reference["generation"]["transcript"],
        "steps": [
            {
                "selected_token_id": item["selected_token_id"],
                "argmax_margin": item["argmax_margin"],
            }
            for item in reference["generation"]["steps"]
        ],
    }
    report = module.validate(reference, graph, runtime)
    assert report["runtime_trace_compared"] is True

    changed = copy.deepcopy(reference)
    changed["generation"]["token_ids"][0] = 7
    expect_failure(changed, graph, "step tokens")

    changed = copy.deepcopy(reference)
    changed["generation"]["steps"][2]["argmax_margin"] = 0.01
    expect_failure(changed, graph, "too fragile")

    changed_graph = copy.deepcopy(graph)
    changed_graph["vocabulary_size"] = 3
    expect_failure(reference, changed_graph, "vocabulary")

    changed_runtime = copy.deepcopy(runtime)
    changed_runtime["token_ids"][4] = 8
    expect_failure(reference, graph, "token IDs differ", changed_runtime)

    changed_runtime = copy.deepcopy(runtime)
    changed_runtime["audio_sha256"] = "0" * 64
    expect_failure(reference, graph, "fixture SHA-256 differs", changed_runtime)

    print("PASS: Moonshine reference evidence validator")


if __name__ == "__main__":
    main()
