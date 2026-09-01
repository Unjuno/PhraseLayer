#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import importlib.util
import json
import pathlib
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
MODULE_PATH = ROOT / "tools/prepare_unity_marian_onnx_assets.py"
spec = importlib.util.spec_from_file_location("prepare_unity_marian_onnx_assets", MODULE_PATH)
assert spec is not None and spec.loader is not None
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def build_fixture(root: pathlib.Path):
    export_dir = root / "export"
    export_dir.mkdir()
    graphs = {}
    for index, name in enumerate(module.EXPECTED_GRAPHS):
        data = (name + f"-{index}").encode("utf-8")
        (export_dir / name).write_bytes(data)
        graphs[name] = {"size_bytes": len(data), "sha256": sha(data)}
    manifest = {
        "schema_version": 1,
        "model_id": module.EXPECTED_MODEL_ID,
        "revision": module.EXPECTED_REVISION,
        "export": {
            "task": "text2text-generation-with-past",
            "framework": "pt",
            "dtype": "fp32",
            "no_post_process": True,
        },
        "onnx": {"graphs": graphs},
    }
    manifest_path = root / "export.json"
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    return export_dir, manifest_path, manifest


def expect_failure(fn, contains: str):
    try:
        fn()
    except module.PrepareError as exc:
        assert contains in str(exc), (contains, str(exc))
    else:
        raise AssertionError("expected PrepareError")


def main() -> None:
    with tempfile.TemporaryDirectory() as temp:
        root = pathlib.Path(temp)
        export_dir, manifest_path, _ = build_fixture(root)
        destination = root / "unity"
        staging_manifest = root / "staging.json"
        report = module.prepare(export_dir, manifest_path, destination, staging_manifest)
        assert report["revision"] == module.EXPECTED_REVISION
        assert [item["name"] for item in report["graphs"]] == list(module.EXPECTED_GRAPHS)
        assert staging_manifest.is_file()
        for name in module.EXPECTED_GRAPHS:
            assert (destination / name).read_bytes() == (export_dir / name).read_bytes()

    with tempfile.TemporaryDirectory() as temp:
        root = pathlib.Path(temp)
        export_dir, manifest_path, _ = build_fixture(root)
        (export_dir / module.EXPECTED_GRAPHS[1]).write_bytes(b"tampered")
        expect_failure(
            lambda: module.prepare(export_dir, manifest_path, root / "unity", root / "staging.json"),
            "identity mismatch before",
        )

    with tempfile.TemporaryDirectory() as temp:
        root = pathlib.Path(temp)
        export_dir, manifest_path, manifest = build_fixture(root)
        manifest["revision"] = "b" * 40
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        expect_failure(
            lambda: module.prepare(export_dir, manifest_path, root / "unity", root / "staging.json"),
            "revision drift",
        )

    with tempfile.TemporaryDirectory() as temp:
        root = pathlib.Path(temp)
        export_dir, manifest_path, manifest = build_fixture(root)
        manifest["onnx"]["graphs"].pop(module.EXPECTED_GRAPHS[-1])
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        expect_failure(
            lambda: module.prepare(export_dir, manifest_path, root / "unity", root / "staging.json"),
            "graph set drift",
        )

    print("PASS: Marian Unity ONNX staging fixtures")


if __name__ == "__main__":
    main()
