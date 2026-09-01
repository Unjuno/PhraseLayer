#!/usr/bin/env python3
"""Static contract for the real-Unity, pre-device Marian product translation gate."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / ".github/workflows/marian-unity-host-gate.yml"
REFERENCE = ROOT / "tools/generate_marian_reference_fixture.py"
STAGER = ROOT / "tools/prepare_unity_marian_assets.py"
EDITOR = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerLocalMarianAssets.cs"
EVIDENCE_EDITOR = ROOT / "unity/PhraseLayer.Unity/Assets/Editor/PhraseLayerMarianParityEvidence.cs"
SHELL = ROOT / "tools/unity/verify-local-marian-translation.sh"
BOOTSTRAP = ROOT / "unity/PhraseLayer.Unity/Assets/Scripts/UnityMarianTranslationBootstrapBehaviour.cs"
GITIGNORE = ROOT / ".gitignore"
LOCK = ROOT / "models/models.lock.json"


class GateError(ValueError):
    pass


def require(text: str, fragment: str, label: str) -> None:
    if fragment not in text:
        raise GateError(f"{label} is missing required marker: {fragment}")


def forbid(text: str, fragment: str, label: str) -> None:
    if fragment in text:
        raise GateError(f"{label} contains forbidden marker: {fragment}")


def validate() -> dict[str, object]:
    workflow = WORKFLOW.read_text(encoding="utf-8")
    reference = REFERENCE.read_text(encoding="utf-8")
    stager = STAGER.read_text(encoding="utf-8")
    editor = EDITOR.read_text(encoding="utf-8")
    evidence_editor = EVIDENCE_EDITOR.read_text(encoding="utf-8")
    shell = SHELL.read_text(encoding="utf-8")
    bootstrap = BOOTSTRAP.read_text(encoding="utf-8")
    ignore = GITIGNORE.read_text(encoding="utf-8")
    lock = json.loads(LOCK.read_text(encoding="utf-8"))

    candidate = next(item for item in lock["candidates"] if item.get("id") == "opus-mt-en-jap")
    revision = candidate["revision"]
    weight_sha = candidate["source_weight_artifact"]["artifact_sha256"]
    if revision != "a863894cdd2b80f3bc1c5966734aee9ffec207d1":
        raise GateError("Marian pinned revision drifted")
    if weight_sha != "4099e38526c3c99dfb5815483e7b556ae96decdffae66f525adda30d8c160738":
        raise GateError("Marian source weight identity drifted")

    for fragment in (
        "HF_HUB_OFFLINE",
        "TRANSFORMERS_OFFLINE",
        'model.to("cpu")',
        'model.eval()',
        "local_files_only=True",
        "num_beams=1",
        "do_sample=False",
        "max_new_tokens=maximum_target_tokens",
        "bad_words_ids=[[EXPECTED_PAD_TOKEN_ID]]",
        "forced_eos_token_id=EXPECTED_EOS_TOKEN_ID",
        "renormalize_logits=True",
        '"source_token_ids"',
        '"generated_token_ids"',
        '"source_weight_sha256"',
        '"phrase-layer-marian-greedy-reference"',
        '"keep off"',
    ):
        require(reference, fragment, "Marian reference generator")
    for forbidden_marker in ("hf_hub_download", "snapshot_download", "requests.get", "urllib.request"):
        forbid(reference, forbidden_marker, "Marian reference generator")

    for fragment in (
        "validate_local_source_snapshot",
        "inspect_bundle",
        'MODEL_FILES = (',
        '"encoder_model.onnx"',
        '"decoder_model.onnx"',
        '"decoder_with_past_model.onnx"',
        '"source.spm", "source.spm.bytes"',
        '"target.spm", "target.spm.bytes"',
        '"vocab.json", "vocab.json"',
        '"marian-reference.json"',
        '"source_weight_copied_to_unity": False',
        '"onnx_contract_inspected": True',
        '"unity_model_root": "Assets/LocalTranslationAssets/Marian"',
        '"unity_tokenizer_resource_root": "LocalTranslationAssets"',
        'for forbidden_name in ("pytorch_model.bin", "model.safetensors")',
        "_copy_verified",
        "staged Marian asset bytes changed",
    ):
        require(stager, fragment, "Marian Unity stager")
    for forbidden_marker in ("hf_hub_download", "snapshot_download", "requests.get", "urllib.request"):
        forbid(stager, forbidden_marker, "Marian Unity stager")

    for fragment in (
        "UnityMarianOnnxContractProbe.ValidateBundle",
        "UnityManagedMarianTokenizerLoader.TryCreateFromResources",
        "ValidateTokenizerReference",
        "RequireExactTokens",
        '"cpu-clone-baseline"',
        "new UnityMarianSeq2SeqGenerationBackend(",
        '"device-resident-cache"',
        "new UnityMarianDeviceResidentGenerationBackend(",
        "OpusMtEnJaGenerationPolicy.CreateGreedyModel(backend)",
        "source_token_ids",
        "generated_token_ids",
        "translated_text",
        "source_weight_copied_to_unity",
        "onnx_contract_inspected",
        "ValidateLanguagePipelineIntegration(tokenizer, assets)",
        'FindSample(assets.Reference, "keep off")',
        "new OfflineSeq2SeqTranslationRuntime(tokenizer, model, options)",
        "new OfflineTranslationEngine(runtime)",
        'learner.SetUnderstanding("keep off", 0.0)',
        'new RuleBasedSemanticSegmenter(new[] { "keep off" })',
        "new LanguagePipeline(",
        "AssistancePolicy.ForMode(AssistanceMode.Balanced)",
        "plan.Assistance.Decisions.Count != 1",
        "plan.Segments.Count != 1 || !plan.Segments[0].IsAssisted",
        'plan.Segments[0].SourceText, "keep off"',
        "plan.DisplayText, sample.translated_text",
        "without added gloss markers",
        "language_pipeline_semantic_replacement=pass",
        "RunTranslationParityProbeBatch",
        "PhraseLayer Marian translation parity PASS",
    ):
        require(editor, fragment, "Marian real-Unity parity probe")

    for fragment in (
        "PhraseLayerLocalMarianAssets.RunTranslationParityProbe()",
        "PHRASELAYER_MARIAN_PARITY_EVIDENCE_PATH",
        "phrase-layer-real-unity-marian-parity",
        "real_unity_execution",
        "model_graph_contract_passed",
        "managed_tokenizer_source_token_parity_passed",
        "cpu_clone_backend_generated_token_parity_passed",
        "device_resident_backend_generated_token_parity_passed",
        "decoded_text_parity_passed",
        "language_pipeline_semantic_replacement_passed",
        "gloss_marker_injection_detected",
        "minimum_reference_samples",
        "File.WriteAllText(evidencePath, json)",
        "RunBatch()",
    ):
        require(evidence_editor, fragment, "Marian Unity parity evidence wrapper")

    for fragment in (
        "Intentionally no -nographics",
        "PHRASELAYER_MARIAN_PARITY_EVIDENCE_PATH",
        "PhraseLayerMarianParityEvidence.RunBatch",
        "Real Unity Marian parity evidence was not produced",
        "real Unity Marian exact-token translation parity for baseline and device-resident backends",
    ):
        require(shell, fragment, "Marian Unity parity shell")

    for fragment in (
        "demo.SetTranslationEngine(engine)",
        "if (demo != null)\n                    demo.enabled = false",
        "UnityMarianDeviceResidentGenerationBackend",
        "OfflineSeq2SeqTranslationRuntime",
    ):
        require(bootstrap, fragment, "Marian product bootstrap")

    for fragment in (
        "workflow_dispatch:",
        "runs-on: [self-hosted, unity, unity-6000-0-66f2]",
        "marian_source_snapshot:",
        "marian_onnx_dir:",
        "prepare_marian_translation.py",
        "export_marian_onnx.py",
        "inspect_marian_onnx_bundle.py",
        "prepare_unity_tokenizer_runtime.py",
        "generate_marian_reference_fixture.py",
        "prepare_unity_marian_assets.py",
        "verify-local-marian-translation.sh",
        "PHRASELAYER_MARIAN_PARITY_EVIDENCE_PATH:",
        "Require Unity-authored Marian parity evidence",
        'assert data["purpose"] == "phrase-layer-real-unity-marian-parity"',
        'assert data["real_unity_execution"] is True',
        'assert unity_parity["language_pipeline_semantic_replacement_passed"] is True',
        'assert unity_parity["gloss_marker_injection_detected"] is False',
        '"unity_parity_evidence_authored_by_unity": True',
        '"product_translation_gate": True',
        '"uploaded_model_weights": False',
        '"quest_device_execution_performed": False',
        "PhraseLayer.marian-unity-parity-evidence.json",
        "phraselayer-marian-unity-host-evidence",
    ):
        require(workflow, fragment, "Marian Unity host workflow")
    for forbidden_marker in (
        "run_quest_read_mode_smoke.py",
        "runs-on: [self-hosted, unity, unity-6000-0-66f2, quest3",
        "pytorch_model.bin\n            ",
        "encoder_model.onnx\n            ",
        "decoder_model.onnx\n            ",
    ):
        forbid(workflow, forbidden_marker, "Marian Unity host workflow")

    require(ignore, "unity/PhraseLayer.Unity/Assets/LocalTranslationAssets/", ".gitignore")
    require(ignore, "unity/PhraseLayer.Unity/Assets/Resources/LocalTranslationAssets/", ".gitignore")

    return {
        "status": "pass",
        "pinned_revision": revision,
        "pinned_source_weight_sha256": weight_sha,
        "offline_reference_required": True,
        "source_token_parity_required": True,
        "generated_token_parity_required": True,
        "decoded_text_parity_required": True,
        "cpu_clone_backend_required": True,
        "device_resident_backend_required": True,
        "language_pipeline_semantic_span_integration_required": True,
        "no_gloss_marker_pipeline_output_required": True,
        "unity_authored_parity_evidence_required": True,
        "real_unity_required": True,
        "source_weight_staged_into_unity": False,
        "model_weights_uploaded_as_artifacts": False,
        "quest_execution_deferred": True,
        "product_translation_gate": True,
    }


def main() -> None:
    print(json.dumps(validate(), sort_keys=True))


if __name__ == "__main__":
    main()
