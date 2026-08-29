#!/usr/bin/env python3
"""Run the Marian tokenizer experiment on a developer machine.

This is the local counterpart of .github/workflows/marian-tokenizer-parity.yml.
It deliberately keeps model weights out of the experiment: only the reviewed
metadata/tokenizer snapshot is fetched or consumed.

The gate performs, in order:
1. bootstrap the pinned Python reference toolchain into an isolated venv;
2. stage or verify the exact revision-pinned small Marian snapshot;
3. inspect source.spm with Google's SentencePiece implementation;
4. generate a Transformers MarianTokenizer reference fixture;
5. run the managed C# tokenizer parity comparison;
6. optionally build/stage the managed tokenizer runtime and run the real Unity
   Editor batch verification when UNITY_EDITOR/--unity-editor is provided.

Generated files live under artifacts/local-marian-tokenizer-parity by default,
which is ignored by git.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from typing import Any, Iterable

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_WORK_DIR = ROOT / "artifacts" / "local-marian-tokenizer-parity"
TOKENIZER_PROJECT = ROOT / "src" / "PhraseLayer.Tokenization.Microsoft" / "PhraseLayer.Tokenization.Microsoft.csproj"
PARITY_PROJECT = ROOT / "tools" / "PhraseLayer.MarianTokenizerParity" / "PhraseLayer.MarianTokenizerParity.csproj"
UNITY_PROJECT = ROOT / "unity" / "PhraseLayer.Unity"
UNITY_VERIFY_METHOD = "PhraseLayer.Unity.Editor.PhraseLayerEditorVerification.VerifyCorePipelineBatch"


class LocalGateError(RuntimeError):
    pass


def log(message: str) -> None:
    print(f"[local-marian] {message}", flush=True)


def run(command: Iterable[os.PathLike[str] | str], *, env: dict[str, str] | None = None) -> None:
    argv = [str(part) for part in command]
    log("RUN " + " ".join(argv))
    completed = subprocess.run(argv, cwd=ROOT, env=env, check=False)
    if completed.returncode != 0:
        raise LocalGateError(
            f"command failed with exit code {completed.returncode}: {' '.join(argv)}"
        )


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise LocalGateError(f"failed to read JSON {path}: {error}") from error


def pinned_revision() -> str:
    lock_path = ROOT / "models" / "models.lock.json"
    lock = load_json(lock_path)
    candidates = [
        item
        for item in lock.get("candidates", [])
        if item.get("id") == "opus-mt-en-jap"
    ]
    if len(candidates) != 1:
        raise LocalGateError("expected exactly one opus-mt-en-jap model lock entry")
    revision = candidates[0].get("revision")
    if not isinstance(revision, str) or len(revision) != 40:
        raise LocalGateError("opus-mt-en-jap must be pinned to a full 40-character revision")
    return revision


def venv_python(venv: Path) -> Path:
    if os.name == "nt":
        return venv / "Scripts" / "python.exe"
    return venv / "bin" / "python"


def ensure_reference_environment(work_dir: Path, *, skip_bootstrap: bool) -> Path:
    if skip_bootstrap:
        log("Using current Python interpreter; dependency bootstrap explicitly skipped")
        return Path(sys.executable)

    requirements = ROOT / "tools" / "requirements-marian-tokenizer-parity.txt"
    requirement_hash = sha256(requirements)
    venv = work_dir / "venv"
    python = venv_python(venv)
    marker = venv / ".phraselayer-requirements-sha256"

    if not python.is_file():
        log(f"Creating isolated Python environment at {venv}")
        run([sys.executable, "-m", "venv", venv])

    installed_hash = marker.read_text(encoding="utf-8").strip() if marker.is_file() else ""
    if installed_hash != requirement_hash:
        run(
            [
                python,
                "-m",
                "pip",
                "install",
                "--disable-pip-version-check",
                "-r",
                requirements,
            ]
        )
        marker.write_text(requirement_hash + "\n", encoding="utf-8")
    else:
        log("Pinned Python reference dependencies already match requirements")

    return python


def verify_snapshot(snapshot_dir: Path, revision: str) -> Path:
    evidence_path = (
        ROOT
        / "models"
        / "evidence"
        / f"opus-mt-en-jap.{revision}.snapshot.json"
    )
    evidence = load_json(evidence_path)
    if evidence.get("revision") != revision:
        raise LocalGateError("committed snapshot evidence revision does not match models.lock.json")

    artifacts = evidence.get("artifacts")
    if not isinstance(artifacts, list) or not artifacts:
        raise LocalGateError("snapshot evidence does not contain an artifact list")

    failures: list[str] = []
    for artifact in artifacts:
        name = artifact.get("name")
        expected_size = artifact.get("size_bytes")
        expected_sha = artifact.get("sha256")
        if not isinstance(name, str):
            failures.append("evidence artifact has invalid name")
            continue
        path = snapshot_dir / name
        if not path.is_file():
            failures.append(f"missing {name}")
            continue
        actual_size = path.stat().st_size
        if actual_size != expected_size:
            failures.append(f"{name}: size {actual_size} != {expected_size}")
            continue
        actual_sha = sha256(path)
        if actual_sha != expected_sha:
            failures.append(f"{name}: sha256 {actual_sha} != {expected_sha}")

    if failures:
        raise LocalGateError("snapshot evidence mismatch:\n  " + "\n  ".join(failures))

    log(f"Verified exact small snapshot against committed evidence: {revision}")
    return evidence_path


def stage_snapshot(
    python: Path,
    work_dir: Path,
    revision: str,
    supplied_snapshot: Path | None,
) -> Path:
    if supplied_snapshot is not None:
        snapshot = supplied_snapshot.resolve()
        if not snapshot.is_dir():
            raise LocalGateError(f"--snapshot-dir does not exist: {snapshot}")
        verify_snapshot(snapshot, revision)
        return snapshot

    snapshot = work_dir / "snapshot"
    live_manifest = work_dir / "live-snapshot.json"
    run(
        [
            python,
            ROOT / "tools" / "fetch_marian_snapshot_metadata.py",
            "--revision",
            revision,
            "--destination",
            snapshot,
            "--output-manifest",
            live_manifest,
        ]
    )
    verify_snapshot(snapshot, revision)

    committed = load_json(
        ROOT
        / "models"
        / "evidence"
        / f"opus-mt-en-jap.{revision}.snapshot.json"
    )
    live = load_json(live_manifest)
    if live != committed:
        raise LocalGateError(
            "freshly generated live snapshot manifest differs from committed evidence"
        )
    log("Fresh live snapshot manifest exactly matches committed evidence")
    return snapshot


def run_parity(python: Path, work_dir: Path, snapshot: Path, revision: str) -> None:
    diagnostic = work_dir / "marian-sentencepiece-diagnostic.json"
    reference = work_dir / "marian-tokenizer-reference.json"

    run(
        [
            python,
            ROOT / "tools" / "inspect_marian_sentencepiece_reference.py",
            "--model",
            snapshot / "source.spm",
            "--output",
            diagnostic,
        ]
    )

    offline_env = dict(os.environ)
    offline_env["HF_HUB_OFFLINE"] = "1"
    offline_env["TRANSFORMERS_OFFLINE"] = "1"
    run(
        [
            python,
            ROOT / "tools" / "generate_marian_tokenizer_reference.py",
            "--snapshot-dir",
            snapshot,
            "--corpus",
            ROOT / "tests" / "fixtures" / "marian-tokenizer-parity-corpus.json",
            "--revision",
            revision,
            "--output",
            reference,
        ],
        env=offline_env,
    )

    run(
        [
            "dotnet",
            "run",
            "--project",
            PARITY_PROJECT,
            "-c",
            "Release",
            "--",
            "--snapshot-dir",
            snapshot,
            "--reference",
            reference,
            "--revision",
            revision,
        ]
    )

    log(f"SentencePiece diagnostic: {diagnostic}")
    log(f"Transformers reference: {reference}")


def run_unity_gate(python: Path, snapshot: Path, revision: str, unity_editor: Path) -> None:
    if not unity_editor.is_file():
        raise LocalGateError(f"Unity Editor executable does not exist: {unity_editor}")

    run(["dotnet", "restore", TOKENIZER_PROJECT])
    run(["dotnet", "build", TOKENIZER_PROJECT, "-c", "Release", "--no-restore"])

    build_output = (
        ROOT
        / "src"
        / "PhraseLayer.Tokenization.Microsoft"
        / "bin"
        / "Release"
        / "netstandard2.1"
    )
    run(
        [
            python,
            ROOT / "tools" / "prepare_unity_tokenizer_runtime.py",
            "--build-output",
            build_output,
        ]
    )
    run(
        [
            python,
            ROOT / "tools" / "prepare_unity_marian_tokenizer_assets.py",
            "--snapshot-dir",
            snapshot,
            "--revision",
            revision,
        ]
    )

    run(
        [
            unity_editor,
            "-batchmode",
            "-nographics",
            "-projectPath",
            UNITY_PROJECT,
            "-executeMethod",
            UNITY_VERIFY_METHOD,
            "-logFile",
            "-",
        ]
    )
    log("Real Unity Editor batch verification passed with staged tokenizer runtime/assets")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run the Marian tokenizer parity and optional Unity import gate locally."
    )
    parser.add_argument(
        "--work-dir",
        type=Path,
        default=DEFAULT_WORK_DIR,
        help="Generated local artifacts/venv location.",
    )
    parser.add_argument(
        "--snapshot-dir",
        type=Path,
        help="Use an existing exact small snapshot instead of downloading it.",
    )
    parser.add_argument(
        "--skip-bootstrap",
        action="store_true",
        help="Use the current Python interpreter instead of creating/updating the pinned venv.",
    )
    parser.add_argument(
        "--unity-editor",
        type=Path,
        help="Path to the Unity 6000.0.66f2 Editor executable. Defaults to UNITY_EDITOR.",
    )
    parser.add_argument(
        "--require-unity",
        action="store_true",
        help="Fail if a real Unity Editor verification cannot be run.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    work_dir = args.work_dir.resolve()
    work_dir.mkdir(parents=True, exist_ok=True)

    if shutil.which("dotnet") is None:
        raise LocalGateError("dotnet was not found on PATH")

    revision = pinned_revision()
    log(f"Pinned model revision: {revision}")
    python = ensure_reference_environment(
        work_dir,
        skip_bootstrap=args.skip_bootstrap,
    )
    snapshot = stage_snapshot(python, work_dir, revision, args.snapshot_dir)

    parity_error: LocalGateError | None = None
    try:
        run_parity(python, work_dir, snapshot, revision)
    except LocalGateError as error:
        # Keep going to the Unity import gate when possible so one local run can
        # report both parity and Unity integration evidence.
        parity_error = error
        print(f"[local-marian] PARITY FAIL: {error}", file=sys.stderr, flush=True)

    editor_value = args.unity_editor or (
        Path(os.environ["UNITY_EDITOR"]) if os.environ.get("UNITY_EDITOR") else None
    )
    if editor_value is not None:
        run_unity_gate(python, snapshot, revision, editor_value.resolve())
    elif args.require_unity:
        raise LocalGateError(
            "Unity verification was required but neither --unity-editor nor UNITY_EDITOR was provided"
        )
    else:
        log("Unity Editor gate skipped; pass --unity-editor or set UNITY_EDITOR to run it")

    if parity_error is not None:
        raise parity_error

    log("PASS: local Marian tokenizer gate completed")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except LocalGateError as error:
        print(f"[local-marian] FAIL: {error}", file=sys.stderr)
        raise SystemExit(1)
