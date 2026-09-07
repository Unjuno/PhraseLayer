"""Differentially inspect the exact locked YAML without publishing a dictionary or relaxing its lock."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import yaml
import extract_ppocr_dictionary as production


def describe(tokens: list[str]) -> dict:
    data = production.dictionary_bytes(tokens)
    return {"tokens": len(tokens), "size_bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--metadata", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    report = {"experiment": "pinned-ppocr-dictionary-independent-YAML-oracle", "status": "uncertain",
              "python": platform.python_version(), "pyyaml_version": yaml.__version__,
              "git_commit": os.getenv("GITHUB_SHA"), "model_inference_performed": False,
              "lock_modified": False, "full_dictionary_uploaded": False}
    result = 1
    try:
        model = production.load_model(production.DEFAULT_LOCK, "pp-ocrv6-tiny-rec")
        contract = production.validate_dictionary_contract(model)
        source = next(item for item in model["support_artifacts"] if item["artifact"] == contract["source_artifact"])
        raw = args.metadata.read_bytes()
        if len(raw) != source["artifact_size_bytes"] or hashlib.sha256(raw).hexdigest() != source["artifact_sha256"]:
            raise ValueError("Metadata identity mismatch")
        text = raw.decode("utf-8")
        document = yaml.safe_load(text)
        oracle = document["PostProcess"]["character_dict"]
        if not isinstance(oracle, list) or not all(isinstance(item, str) for item in oracle):
            raise ValueError("Independent loader returned non-string dictionary entries")
        actual = production.extract_tokens(text, contract)
        differences = []
        for index in range(max(len(oracle), len(actual))):
            left = oracle[index] if index < len(oracle) else None
            right = actual[index] if index < len(actual) else None
            if left != right and len(differences) < 16:
                differences.append({"index": index,
                    "oracle_codepoints": None if left is None else [ord(character) for character in left],
                    "production_codepoints": None if right is None else [ord(character) for character in right]})
        measured = describe(actual)
        reference = describe(oracle)
        # Candidate encodings are diagnostic only. Never use one of these to rewrite the contract automatically.
        without_last_newline = production.dictionary_bytes(oracle)[:-1]
        trimmed_tokens = [item.strip() for item in oracle]
        report.update(status="completed", metadata_identity_verified=True,
            metadata_sha256=hashlib.sha256(raw).hexdigest(), production=measured, oracle=reference,
            expected={"tokens": contract["raw_token_count"], "size_bytes": contract["generated_artifact_size_bytes"], "sha256": contract["generated_artifact_sha256"]},
            production_matches_oracle=actual == oracle, differences=differences,
            oracle_matches_lock=reference["size_bytes"] == contract["generated_artifact_size_bytes"] and reference["sha256"] == contract["generated_artifact_sha256"],
            no_terminal_newline={"size_bytes": len(without_last_newline), "sha256": hashlib.sha256(without_last_newline).hexdigest()},
            trimmed_tokens=describe(trimmed_tokens),
            multi_codepoint_entries=[{"index": i, "codepoints": [ord(c) for c in token]} for i, token in enumerate(oracle) if len(token) != 1][:32])
        result = 0 if report["production_matches_oracle"] and report["oracle_matches_lock"] else 1
        report["safety_result"] = "PASS" if result == 0 else "FAIL"
    except Exception as error:
        report.update(status="failed", error_type=type(error).__name__, error=str(error))
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2, ensure_ascii=True) + "\n")
    print(json.dumps(report, ensure_ascii=True))
    return result


if __name__ == "__main__":
    raise SystemExit(main())
