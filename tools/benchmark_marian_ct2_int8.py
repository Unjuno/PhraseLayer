#!/usr/bin/env python3
"""Offline-only, opt-in CTranslate2 INT8 candidate evaluation for pinned Marian en->jap.

This is NOT a Quest/Unity runtime and NEVER promotes the INT8 candidate.
Both conversion products live in a disposable temporary directory by default.
No network fetch, source text, decoded translations, weights or tokenizer files
are written to the report. A local, exact-hash source snapshot is required.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import pathlib
import statistics
import tempfile
import time

REPO = pathlib.Path(__file__).resolve().parents[1]
LOCK = REPO / "models" / "models.lock.json"
POLICY = REPO / "models" / "candidates" / "marian-ct2-int8.json"
# Phrases reflect the in-world semantic-unit product, not generic document translation.
PHRASES = (
    "keep off", "emergency exit", "do not enter", "no smoking",
    "employees only", "wet floor", "no parking", "please wait here",
    "restricted area", "watch your step", "fire exit", "out of order",
    "staff only", "open daily", "closed today", "turn left",
    "turn right", "thank you", "I was tired.", "I will be back.",
    "Please do not touch.", "Do not block the door.",
    "Please keep your distance.", "The museum closes at five.",
)
CRITICAL = frozenset(range(12))
MAX_DECODE_TOKENS = 64
SOURCE_FILES = frozenset((
    "README.md", "config.json", "generation_config.json",
    "tokenizer_config.json", "source.spm", "target.spm",
    "vocab.json", "pytorch_model.bin",
))


class CandidateRejected(ValueError):
    pass


def require(condition: bool, why: str) -> None:
    if not condition:
        raise CandidateRejected(why)


def file_hash(path: pathlib.Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            sha.update(block)
    return sha.hexdigest()


def load_policy_and_lock(lock_path: pathlib.Path = LOCK,
                         policy_path: pathlib.Path = POLICY) -> tuple[dict, dict]:
    lock = json.loads(lock_path.read_text(encoding="utf-8"))
    policy = json.loads(policy_path.read_text(encoding="utf-8"))
    entries = [x for x in lock["candidates"] if x["id"] == "opus-mt-en-jap"]
    require(len(entries) == 1, "expected one locked Marian source")
    source = entries[0]
    require(policy.get("schema_version") == 1, "unsupported experimental policy schema")
    require(policy.get("upstream") == source["upstream"], "candidate upstream drift")
    require(policy.get("revision") == source["revision"], "candidate revision drift")
    require(policy.get("quantization") == "int8_float32", "not the reviewed INT8 compute type")
    require(policy.get("target") == "aarch64-cpu-experimental", "target mismatch")
    require(policy.get("production_enabled") is False, "experimental candidate cannot be active")
    require(policy.get("bundled") is False, "candidate must remain unbundled")
    require(policy.get("redistribution_review_pending") is True,
            "redistribution gating must remain explicit")
    require(source["license"].lower() == "apache-2.0",
            "locked Marian model license must be reviewed")
    return source, policy


def verify_locked_file(path: pathlib.Path, name: str, size: int, digest: str) -> None:
    require(path.is_file(), "missing required local snapshot file: " + name)
    require(path.stat().st_size == size, "locked size mismatch: " + name)
    require(file_hash(path) == digest, "locked SHA-256 mismatch: " + name)


def verify_local_snapshot(snapshot: pathlib.Path, source: dict) -> dict:
    require(snapshot.is_dir(), "local snapshot directory not found")
    expected = {
        x["artifact"]: (x["artifact_size_bytes"], x["artifact_sha256"])
        for x in source["metadata_snapshot_artifacts"]
    }
    weight = source["source_weight_artifact"]
    expected[weight["artifact"]] = (
        weight["artifact_size_bytes"], weight["artifact_sha256"])
    require(set(expected) == SOURCE_FILES, "reviewed source file list changed")
    for name, (size, digest) in expected.items():
        verify_locked_file(snapshot / name, name, size, digest)
    # No remote access, potentially untrusted transformers code or new source files.
    require(not (snapshot / "trust_remote_code.py").exists(),
            "unexpected executable model code in snapshot")
    return {"source_revision": source["revision"],
            "source_weight_bytes": weight["artifact_size_bytes"],
            "source_weight_sha256": weight["artifact_sha256"],
            "validated_source_file_count": len(expected)}


def byte_size(directory: pathlib.Path) -> int:
    return sum(p.stat().st_size for p in directory.rglob("*") if p.is_file())


def safe_japanese(text: str) -> bool:
    return any(("\u3040" <= ch <= "\u30ff") or ("\u3400" <= ch <= "\u9fff")
               for ch in text)


def evaluate_outputs(baseline: list[str], int8: list[str],
                     baseline_bytes: int, int8_bytes: int,
                     critical: frozenset[int] = CRITICAL) -> dict:
    require(len(baseline) == len(int8) and len(baseline) > 0,
            "candidate output length mismatch")
    require(baseline_bytes > 0 and int8_bytes > 0, "empty converted model")
    require(all(isinstance(s, str) for s in baseline + int8),
            "invalid model output type")
    n = len(baseline)
    agreements = [a == b for a, b in zip(baseline, int8)]
    valid = [bool(s.strip()) and "\ufffd" not in s for s in int8]
    japanese = [safe_japanese(s) for s in int8]
    critical_ok = all(0 <= index < n and agreements[index] and valid[index]
                      and japanese[index] for index in critical)
    ratio = int8_bytes / baseline_bytes
    # Conservative acceptance for *device trial*, never automatic promotion.
    eligible = (ratio <= 0.65 and sum(agreements) / n >= 0.95
                and all(valid) and critical_ok)
    return {
        "case_count": n,
        "exact_agreement_cases": sum(agreements),
        "exact_agreement_ratio": round(sum(agreements) / n, 6),
        "empty_or_invalid_outputs": valid.count(False),
        "critical_case_count": len(critical),
        "critical_case_agreements": sum(agreements[i] for i in critical if 0 <= i < n),
        "critical_japanese_script_cases": sum(japanese[i] for i in critical if 0 <= i < n),
        "reference_ct2_float32_bytes": baseline_bytes,
        "candidate_ct2_int8_bytes": int8_bytes,
        "candidate_to_reference_storage_ratio": round(ratio, 6),
        "eligible_for_device_trial": eligible,
        "production_model_replaced": False,
        "real_unity_tested": False,
        "android_abi_tested": False,
        "quest_latency_measured": False,
        "translation_accuracy_measured": False,
    }


def quantile(values: list[float], portion: float) -> float:
    require(bool(values) and 0 <= portion <= 1, "invalid quantile input")
    s = sorted(values)
    i = math.ceil(portion * len(s)) - 1
    return round(s[max(0, i)] * 1000, 3)


def convert_and_benchmark(snapshot: pathlib.Path,
                          temporary_root: pathlib.Path) -> tuple[dict, dict]:
    # The imports are deliberately after hash verification. Nothing is fetched.
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_DATASETS_OFFLINE"] = "1"
    import ctranslate2
    from transformers import MarianTokenizer

    baseline_dir = temporary_root / "baseline-fp32"
    int8_dir = temporary_root / "candidate-int8"
    for dest, quant in ((baseline_dir, "float32"), (int8_dir, "int8_float32")):
        # No --force; an existing directory cannot be silently reused.
        converter = ctranslate2.converters.TransformersConverter(str(snapshot))
        converter.convert(str(dest), quantization=quant)
        require(ctranslate2.contains_model(str(dest)), "CT2 conversion missing model")
    tokenizer = MarianTokenizer.from_pretrained(str(snapshot), local_files_only=True)
    options = dict(device="cpu", inter_threads=1, intra_threads=2)
    baseline = ctranslate2.Translator(str(baseline_dir),
                                     compute_type="float32", **options)
    candidate = ctranslate2.Translator(str(int8_dir),
                                      compute_type="int8_float32", **options)
    pretokenized = [tokenizer.convert_ids_to_tokens(tokenizer.encode(s))
                    for s in PHRASES]
    def run(translator, tokens):
        output_tokens = translator.translate_batch(
            [tokens], beam_size=1, max_decoding_length=MAX_DECODE_TOKENS,
            return_scores=False)[0].hypotheses[0]
        return tokenizer.decode(
            tokenizer.convert_tokens_to_ids(output_tokens),
            skip_special_tokens=True, clean_up_tokenization_spaces=False)

    # Preheat both CPU runtimes; host timings are not a Quest claim.
    for engine in (baseline, candidate):
        for tokens in pretokenized[:2]:
            run(engine, tokens)
    timings = {}
    results = {}
    for name, engine in (("float32", baseline), ("int8", candidate)):
        latencies = []
        outputs = []
        for tokens in pretokenized:
            t = time.perf_counter()
            output = run(engine, tokens)
            latencies.append(time.perf_counter() - t)
            outputs.append(output)
        timings[name] = {"host_p50_ms": quantile(latencies, 0.50),
                         "host_p95_ms": quantile(latencies, 0.95)}
        results[name] = outputs
    summary = evaluate_outputs(results["float32"], results["int8"],
                               byte_size(baseline_dir), byte_size(int8_dir))
    # No source text or decoded translations are ever written to disk.
    return summary, {"per_backend": timings,
                     "measurement_scope": "host-cpu-only-not-quest"}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot", required=True, type=pathlib.Path,
                        help="Exact local locked Marian source snapshot (no downloading)")
    parser.add_argument("--report", required=True, type=pathlib.Path,
                        help="Numerical JSON only; no model data or translations")
    args = parser.parse_args()
    source, policy = load_policy_and_lock()
    identity = verify_local_snapshot(args.snapshot, source)
    with tempfile.TemporaryDirectory(prefix="phraselayer-ct2-") as work:
        result, runtime = convert_and_benchmark(args.snapshot, pathlib.Path(work))
    report = {
        "schema_version": 1, "experiment": "marian-ct2-int8-host-candidate",
        "status": "experiment_completed_not_product_approval",
        "source": identity, "candidate": policy["id"],
        "quantization": policy["quantization"],
        "measurements": result, "host_timing": runtime,
        "source_weights_downloaded": False,
        "model_or_translation_uploaded": False,
        "device_execution_performed": False,
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n",
                           encoding="utf-8")
    # Fail exit when the candidate did not qualify, but retain the numerical report.
    print(json.dumps({"status": report["status"],
                      "eligible_for_device_trial": result["eligible_for_device_trial"],
                      "candidate_to_reference_storage_ratio":
                          result["candidate_to_reference_storage_ratio"]},
                     sort_keys=True))
    if not result["eligible_for_device_trial"]:
        raise SystemExit(2)


if __name__ == "__main__":
    main()
