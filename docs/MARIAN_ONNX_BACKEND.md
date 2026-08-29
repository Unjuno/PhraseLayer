# Marian ONNX backend experiment

PhraseLayer's production translation path is intended to remain offline-first and replaceable. This experiment prepares Unity Inference Engine backends for the reviewed `Helsinki-NLP/opus-mt-en-jap` Marian model without committing model weights.

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

The general graph contract records extra exporter inputs and outputs instead of confusing them with missing required Marian semantics. Execution is stricter: `OpusMtEnJaMarianOnnxExecutionContract` rejects every required input that the current backend does not explicitly bind. For example, a future `cache_position` input is not silently ignored; support must be implemented and tested first. Extra outputs are harmless and remain allowed.

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
2. `last_hidden_state` is cloned into an owned CPU tensor;
3. the first decoder step creates all six layers of self-attention and cross-attention KV cache;
4. cache tensors are cloned into owned CPU tensors before the producing Worker is rescheduled;
5. later steps use only the latest decoder token plus `decoder_with_past_model` and the retained cache;
6. each logits tensor is validated as `[1, T, 46276]`, and only the final time step is returned to Core's greedy generator.

This baseline remains useful as a parity/reference path because every tensor lifetime is explicit.

A shape-only container estimate demonstrates why it is not the intended Quest path. For float32 caches, six layers, eight heads and head dimension 64, self-attention cache grows by 24 KiB per generated token. At source length 64 and target length 48, cumulative CPU self-cache cloning plus one cross-cache clone, encoder-state clone and logits readback is approximately 37.7 MiB. This is a theoretical byte count derived from tensor geometry, not a hardware bandwidth or latency measurement.

## Device-resident cache experiment

`UnityMarianDeviceResidentGenerationBackend` is an alternative experimental backend based on the actual Inference Engine 2.2.1 Worker ownership contract.

The reviewed 2.2.1 package source states that a `PeekOutput` reference remains valid until the producing Worker is scheduled again, scheduled iteratively, or disposed. Its model storage also treats tensors supplied through `SetInput` as caller-owned inputs. `Worker.CopyOutput` creates an independent backend tensor by performing a backend memory copy.

The experiment therefore uses these rules:

1. encoder output is copied with `Worker.CopyOutput` so the encoder Worker can be disposed without CPU readback;
2. the first decoder Worker is scheduled exactly once and retained for the session;
3. its cross-attention cache is reused directly through `PeekOutput` references because that Worker is never rescheduled;
4. its first-step self-attention cache is also used directly for the second decoder step;
5. after every `decoder_with_past` step, only self-attention cache is copied with `Worker.CopyOutput` before that Worker can be rescheduled;
6. cross-attention cache remains the immutable first-decoder cache;
7. only logits are read back to CPU for the current Core greedy argmax.

This removes CPU readback of encoder state and KV cache while preserving the documented output-lifetime boundary. It is still experimental until the real Unity 6000.0.66f2 runner executes it with imported models and numerical parity tests.

## Hosted validation

Automated without model weights:

- Core graph-name/cache contract tests;
- Core executable-input contract tests;
- Python synthetic ONNX signature tests;
- ordinary Unity fallback shell compilation;
- a dedicated hosted compile shell that defines `PHRASELAYER_UNITY_AI_INFERENCE_2_2` and compiles both Marian execution backends against a narrow API surface mirroring Inference Engine 2.2.1;
- local cache naming/shape and theoretical transfer calculations in the available Python container.

The hosted API shell is intentionally not described as a real Unity package compile. Exact 2.2.1 package source was separately inspected for the APIs used by these experiments: `Worker(Model, BackendType)`, `SetInput`, `Schedule`, `PeekOutput`, `CopyOutput`, `Tensor<T>.ReadbackAndClone`, and `DownloadToArray`.

## Still required

- revision-pinned source snapshot and full 40-character upstream SHA;
- actual `optimum-cli export onnx` output inspection;
- tokenizer parity against Transformers on the exact snapshot;
- real Unity 6000.0.66f2 compilation of the `PHRASELAYER_UNITY_AI_INFERENCE_2_2` execution branch;
- actual imported ONNX ModelAssets;
- numerical translation parity against a trusted reference;
- Android IL2CPP build;
- Quest 3 latency, allocation, memory and thermal measurements;
- comparison of CPU-clone baseline versus device-resident cache backend on Quest hardware.

No LLM is introduced by this work. The target remains a dedicated Marian encoder/decoder translation model.
