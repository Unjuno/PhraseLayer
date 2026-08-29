# Offline English → Japanese translation runtime

PhraseLayer's product path requires arbitrary English → Japanese translation without a mandatory network dependency. The dictionary engine remains useful for deterministic demos, but the production path is the reviewed offline Marian stack described here.

This document distinguishes host-tested contracts from real Unity/Quest execution. No statement below should be read as a Quest 3 performance claim.

## Runtime boundary

`LanguagePipeline` continues to depend only on `ITranslationEngine`.

```text
LanguagePipeline
      ↓
ITranslationEngine
      ↓
OfflineTranslationEngine
      ↓
OfflineSeq2SeqTranslationRuntime
   ↙                         ↘
ITranslationTokenizer   ISeq2SeqTranslationModel
      ↓                         ↓
MarianSentencePiece      GreedySeq2SeqTranslationModel
Tokenizer                       ↓
      ↓                  ISeq2SeqGenerationBackend
ISentencePieceProcessor          ↓
                         Unity encoder/decoder + KV cache
```

`OfflineSeq2SeqTranslationRuntime` translates the exact semantic span requested by `LanguagePipeline`. The surrounding sentence remains available as `OfflineTranslationRequest.Context`, but the baseline runtime does not silently concatenate context into the model input because doing so would change the semantic span being translated.

OCR, semantic selection, tokenization, generation, and Unity model execution remain separate contracts so each boundary can be parity-tested independently.

## Pinned candidate: Helsinki-NLP/opus-mt-en-jap

PhraseLayer now pins the candidate to the full upstream revision:

```text
a863894cdd2b80f3bc1c5966734aee9ffec207d1
```

The committed evidence manifest is:

```text
models/evidence/opus-mt-en-jap.a863894cdd2b80f3bc1c5966734aee9ffec207d1.snapshot.json
```

The following seven small upstream artifacts were fetched from that exact revision, contract-validated, SHA-256 fingerprinted, and then discarded rather than bundled:

```text
README.md
config.json
generation_config.json
tokenizer_config.json
source.spm
target.spm
vocab.json
```

`models/models.lock.json` must match that committed evidence exactly. `tools/validate_marian_lock_evidence.py` rejects revision, license, artifact-name, size, SHA-256, language, generation-policy, allow-list, or `weights_downloaded` drift.

Reviewed candidate properties:

| Field | Pinned value |
|---|---:|
| architecture | `MarianMTModel` |
| model type | `marian` |
| source language | `en` |
| target language | `jap` |
| vocabulary | 46,276 |
| model dimension | 512 |
| encoder layers | 6 |
| decoder layers | 6 |
| attention heads | 8 |
| head dimension | 64 |
| maximum positions | 512 |
| BOS / EOS | 0 / 0 |
| PAD / decoder start | 46,275 / 46,275 |
| upstream configured beams | 4 |
| bad generation token | 46,275 |
| forced EOS | 0 |
| preprocessing | normalization + SentencePiece |

The exact-revision model card declares `apache-2.0`. PhraseLayer records that fact as pinned metadata evidence. **Redistribution review remains pending**, so no model weight is committed or bundled by this work.

## Source-weight identity

The expected upstream PyTorch source weight identity is recorded separately in the lock:

```text
pytorch_model.bin
size:   273,663,309 bytes
sha256: 4099e38526c3c99dfb5815483e7b556ae96decdffae66f525adda30d8c160738
```

This identity is derived from the upstream LFS history and is intentionally treated differently from the seven-file committed evidence. A local full snapshot is not accepted for export until PhraseLayer hashes the actual local `pytorch_model.bin` and obtains an exact size/SHA match.

No PhraseLayer tool automatically downloads that weight.

## Marian tokenizer boundary

`MarianSentencePieceTokenizer` maps exact SentencePiece piece strings through the external `vocab.json`; it never assumes that raw SentencePiece internal ids equal Marian vocabulary ids.

```text
source text
  ↓ exact source.spm processor
SentencePiece piece strings
  ↓ external vocab.json lookup
Marian model token ids
  ↓ reserve one slot + append EOS
encoder input ids
```

Target decoding reverses the external vocabulary mapping, removes generation-only EOS/PAD (and optional `<eop>` / `<eod>`) tokens, and delegates piece reconstruction to the exact target SentencePiece processor.

A managed Microsoft tokenizer adapter and Unity late-bound loader are implemented and host-tested. What is still required is **reference parity against Transformers/MarianTokenizer on the exact pinned snapshot**, including punctuation, apostrophes, Unicode normalization, numbers, whitespace, and unknown text.

## Generation policy

`GreedySeq2SeqTranslationModel` owns the platform-neutral generation loop. PhraseLayer's correctness-parity baseline deliberately uses beam width 1 even though the upstream configuration uses beam width 4.

```text
source ids
  ↓ encoder once
ISeq2SeqGenerationSession
  ↓ decoder_start_token = 46275
DecodeNextAsync
  ↓ logits[46276]
finite/shape validation
  ↓ ban token 46275
argmax
  ↓
next cached decoder step
  ↓
EOS 0 or maximum target tokens
```

The final target slot can force EOS 0. PAD/decoder-start token 46275 is banned from generated output, matching the pinned upstream `bad_words_ids` policy. Beam search remains a separate quality/performance decision and is not silently approximated by the greedy path.

## Marian ONNX graph contract

PhraseLayer expects the explicit Optimum seq2seq-with-past export:

```text
encoder_model.onnx
decoder_model.onnx
decoder_with_past_model.onnx
```

`OpusMtEnJaMarianOnnxContract` and `tools/inspect_marian_onnx_bundle.py` validate:

- encoder `input_ids` / `attention_mask` → `last_hidden_state`;
- first decoder inputs and `logits`;
- decoder-with-past inputs and `logits`;
- all six decoder layers;
- self-attention and cross-attention key/value cache names;
- 8 heads × 64 head dimension;
- model dimension 512;
- vocabulary dimension 46,276;
- all-or-none cross-attention cache outputs from the cached decoder;
- unsupported cache-layer drift.

The Unity preflight additionally rejects graph inputs that the current backend cannot bind, rather than accepting an exporter change and failing later inside `Worker.Schedule`.

## Unity generation backends

Two Unity Inference Engine 2.2.x implementations exist behind the platform boundary:

1. `UnityMarianSeq2SeqGenerationBackend` — correctness-first CPU readback/clone baseline.
2. `UnityMarianDeviceResidentGenerationBackend` — experimental backend-resident encoder/KV cache path intended to reduce CPU transfer.

The device-resident experiment is based on the verified Unity Inference Engine 2.2.1 `Worker`/`Tensor` API semantics: a `PeekOutput` reference remains valid until that same Worker is scheduled again or disposed, and `CopyOutput` can make an independently retained backend tensor. Hosted CI compiles the guarded 2.2.x execution branch against a purpose-built shell, but that is not equivalent to importing/running the real package in Unity 6000.0.66f2.

`UnityMarianTranslationBootstrapBehaviour` can inject the Marian `OfflineTranslationEngine` into the existing Demo/Live Read Mode pipeline. When explicitly configured for Marian and required runtime assets are missing, it disables the demo instead of silently falling back to `DictionaryTranslationEngine`.

## Revision-pinned metadata staging

`tools/fetch_marian_snapshot_metadata.py` is the only online metadata helper. It:

- resolves a supplied Hub ref to a full 40-character commit SHA;
- downloads **only** the seven reviewed small artifacts;
- rejects a destination containing weights or ONNX graphs;
- validates the exact snapshot contract;
- emits SHA-256 evidence with `weights_downloaded=false`.

The normal CI path does not depend on Hugging Face. `.github/workflows/marian-snapshot-probe.yml` is manual-only and re-fetches the **exact locked revision**, then requires the live evidence JSON to equal the committed evidence JSON exactly.

## Lock-bound offline ONNX export

The preferred export entry point is `tools/export_marian_onnx.py`. It never downloads model files.

Before export it requires:

1. committed lock/evidence integrity;
2. all seven local metadata/tokenizer artifacts to exactly match committed evidence;
3. local `pytorch_model.bin` to exactly match the locked size/SHA;
4. an exact reviewed export-toolchain version set;
5. an empty output directory.

The reviewed CPU export environment is pinned in:

```text
tools/requirements-marian-export.txt
```

Current pins include Optimum ONNX 0.1.0 / Optimum 2.1.0 / Transformers 4.57.6 and the corresponding explicitly pinned PyTorch, ONNX Runtime, ONNX, SentencePiece, and Sacremoses tooling.

The command shape is:

```text
optimum-cli export onnx \
  --model <EXACT_LOCAL_PINNED_SNAPSHOT> \
  --task text2text-generation-with-past \
  --framework pt \
  --dtype fp32 \
  --no-post-process \
  <EMPTY_OUTPUT_DIR>
```

On execution the wrapper forces:

```text
HF_HUB_OFFLINE=1
TRANSFORMERS_OFFLINE=1
HF_DATASETS_OFFLINE=1
```

After Optimum returns, the wrapper immediately runs `inspect_marian_onnx_bundle.py`. A combined source/toolchain/export/graph evidence manifest is produced only if the graph contract passes.

`tools/prepare_marian_translation.py` remains an evidence/fingerprinting helper for local experiments; it is **not** the preferred execution path and does not replace the lock-bound exporter.

## Validation gates

The remaining translation gates are intentionally ordered:

1. **Pinned metadata identity** — complete; exact revision + seven-file hashes committed.
2. **Pinned model-card license metadata** — complete for the exact revision; redistribution review still pending.
3. **Source-weight identity contract** — recorded; actual local weight must be rehashed before export.
4. **SentencePiece parity** — compare the managed runtime to Transformers/MarianTokenizer on the exact snapshot.
5. **Actual ONNX export** — run the offline lock-bound exporter against an exact local full snapshot.
6. **ONNX graph inspection** — implemented; must pass against the actual exported files.
7. **Greedy numerical parity** — compare token-by-token/logit-selection output with a trusted Transformers reference under `num_beams=1`, `do_sample=false`.
8. **Real Unity import/execution** — Unity 6000.0.66f2 + `com.unity.ai.inference@2.2.1`.
9. **Android IL2CPP** — build and execute the same path on Android target.
10. **Quest 3 measurements** — cold/warm latency, allocation, memory, frame impact, and thermal behavior.
11. **Translation quality** — review phrase-level fixtures for signs, menus, instructions, labels, and ordinary prose; decide whether beam search is justified.

## Current status

Implemented and host-tested:

- offline seq2seq orchestration and `ITranslationEngine` adapter;
- managed SentencePiece runtime boundary;
- strict candidate metadata/generation contract;
- full upstream revision resolution;
- exact-revision model-card/config/tokenizer evidence and cryptographic lock binding;
- local source-weight identity gate;
- strict three-graph Marian ONNX contract;
- CPU-clone and experimental device-resident Unity generation backends;
- Unity Marian bootstrap into Demo/Live Read Mode;
- exact export-toolchain pin file;
- offline lock-bound Optimum export wrapper;
- synthetic tests for metadata, lock/evidence, source-weight, exporter-command, toolchain-drift, stale-output, and ONNX graph contracts;
- normal Unity shell and guarded Marian Unity shell compilation in hosted CI.

Not yet demonstrated:

- managed-tokenizer parity against the exact Transformers reference;
- actual local full-source snapshot verification with the 273 MB weight;
- actual Optimum ONNX export from that source snapshot;
- numerical translation parity;
- real Unity 6000.0.66f2 model import/execution;
- Android IL2CPP execution;
- Quest 3 latency/memory/thermal measurements;
- redistribution approval for bundling model files.

No model weight is committed or bundled by this work.
