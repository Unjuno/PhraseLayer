# Marian ONNX backend experiment

PhraseLayer's production translation path is intended to remain offline-first and replaceable. This experiment prepares a correctness-first Unity Inference Engine backend for the reviewed `Helsinki-NLP/opus-mt-en-jap` Marian model without committing model weights.

## Export shape

The reviewed local export remains the three-file Optimum seq2seq-with-past layout:

```text
encoder_model.onnx
decoder_model.onnx
decoder_with_past_model.onnx
```

The Core `OpusMtEnJaMarianOnnxContract` validates semantic graph names before Unity allocates a generation session.

Required encoder contract:

```text
input_ids
attention_mask
  -> last_hidden_state
```

Required first decoder contract:

```text
input_ids
encoder_hidden_states
encoder_attention_mask
  -> logits
  -> present.{0..5}.{decoder|encoder}.{key|value}
```

Required cached decoder contract:

```text
input_ids
encoder_hidden_states
encoder_attention_mask
past_key_values.{0..5}.{decoder|encoder}.{key|value}
  -> logits
  -> present.{0..5}.decoder.{key|value}
```

Some Optimum exports also return `present.*.encoder.*` from the cached decoder. PhraseLayer accepts either all six cross-attention cache pairs or none; partial cross-cache output is rejected.

Extra exporter inputs/outputs are allowed so explicit additions such as `cache_position` can be reviewed without weakening the required Marian semantic contract.

## Reviewed model geometry

Current reviewed metadata for the candidate is:

- decoder layers: 6;
- encoder/decoder attention heads: 8;
- model dimension: 512;
- per-head dimension: 64;
- vocabulary size: 46,276;
- maximum positions: 512.

The local `tools/inspect_marian_onnx_bundle.py` tool checks real ONNX graph dtype/rank/fixed dimensions when the optional ONNX tooling package is available. It emits only a small evidence manifest with graph signatures, file sizes and SHA-256 hashes. The ONNX weights themselves remain local and git-ignored.

## Unity runtime baseline

`UnityMarianSeq2SeqGenerationBackend` implements Core `ISeq2SeqGenerationBackend` behind the reviewed Unity Inference Engine 2.2.x compile gate.

The baseline intentionally prioritizes ownership correctness over performance:

1. encoder input ids and attention mask are executed once;
2. `last_hidden_state` is cloned into an owned tensor;
3. the first decoder step creates all six layers of self-attention and cross-attention KV cache;
4. cache tensors are cloned into owned tensors before the producing Worker is rescheduled;
5. later steps use only the latest decoder token plus `decoder_with_past_model` and the retained cache;
6. each logits tensor is validated as `[1, T, 46276]`, and only the final time step is returned to Core's greedy generator.

This CPU readback/clone path is intentionally not the final Quest optimization. It avoids assuming that a `PeekOutput` tensor remains valid after the same Worker is rescheduled. Once output parity is established, device-resident cache reuse can be introduced behind the same Core interface and measured separately.

## Validation boundaries

Automated without model weights:

- Core graph-name/cache contract tests;
- Python synthetic ONNX signature tests;
- Unity fallback shell compilation;
- local cache naming/shape fixture in the available container.

Still required:

- revision-pinned source snapshot and full 40-character upstream SHA;
- actual `optimum-cli export onnx` output inspection;
- tokenizer parity against Transformers on the exact snapshot;
- real Unity 6000.0.66f2 compilation of the `PHRASELAYER_UNITY_AI_INFERENCE_2_2` execution branch;
- actual imported ONNX ModelAssets;
- numerical translation parity against a trusted reference;
- Android IL2CPP build;
- Quest 3 latency, allocation, memory and thermal measurements;
- a device-resident cache path if CPU cache cloning is too expensive.

No LLM is introduced by this work. The target remains a dedicated Marian encoder/decoder translation model.
