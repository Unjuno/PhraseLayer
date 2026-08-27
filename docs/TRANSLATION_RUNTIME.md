# Offline English → Japanese translation runtime

PhraseLayer's product path requires arbitrary English → Japanese translation without a mandatory network dependency. The current dictionary engine is useful for deterministic demos but is not a production translation runtime.

This document defines the next runtime boundary without claiming that a candidate model already runs on Quest 3.

## Core boundary

`LanguagePipeline` continues to depend only on `ITranslationEngine`.

```text
LanguagePipeline
      ↓
ITranslationEngine
      ↓
OfflineTranslationEngine
      ↓
IOfflineTranslationRuntime
      ↓
OfflineSeq2SeqTranslationRuntime
   ↙                         ↘
ITranslationTokenizer   ISeq2SeqTranslationModel
```

`OfflineSeq2SeqTranslationRuntime` translates the exact semantic span requested by `LanguagePipeline`. The surrounding sentence remains available as `OfflineTranslationRequest.Context`, but the baseline runtime does not silently concatenate context into the model input because doing so would change the span being translated.

The tokenizer and model are separate contracts so a managed/native SentencePiece implementation and a Unity Inference encoder/decoder implementation can be validated independently.

## Candidate: Helsinki-NLP/opus-mt-en-jap

Observed upstream characteristics used by `OpusMtEnJaMarianContract`:

| Field | Expected value |
|---|---:|
| architecture | `MarianMTModel` |
| model type | `marian` |
| source language | `en` |
| target language | `jap` |
| vocabulary | 46,276 |
| model dimension | 512 |
| encoder layers | 6 |
| decoder layers | 6 |
| maximum positions | 512 |
| BOS / EOS | 0 / 0 |
| PAD / decoder start | 46,275 / 46,275 |
| configured beams | 4 |
| preprocessing | normalization + SentencePiece |

The upstream repository currently exposes a short latest revision id `a863894`. PhraseLayer does **not** treat that short id as a reproducible pin. A full 40-character revision must be recorded before model artifacts are staged or redistributed.

The model candidate is Apache-2.0 according to upstream metadata, but redistribution remains a separate review gate.

## Reproducible local preparation

`tools/prepare_marian_translation.py` intentionally performs no network download. It consumes a locally available, revision-pinned upstream snapshot and validates:

- `config.json`;
- `generation_config.json`;
- `tokenizer_config.json`;
- `vocab.json` and contiguous 0..46,275 ids;
- `source.spm` and `target.spm` presence;
- source weight presence;
- exact Marian architecture/token ids/layer dimensions;
- a full 40-character revision supplied by the operator.

Every supplied file is SHA-256 fingerprinted into a generated manifest.

### ONNX export shape

The reviewed export recipe is intentionally explicit:

```text
optimum-cli export onnx \
  --model <REVISION_PINNED_LOCAL_SNAPSHOT> \
  --task text2text-generation-with-past \
  --no-post-process \
  <OUTPUT_DIR>
```

PhraseLayer expects three graphs from that recipe:

```text
encoder_model.onnx
decoder_model.onnx
decoder_with_past_model.onnx
```

`--no-post-process` is used so decoder and decoder-with-past remain separately inspectable instead of relying on an exporter merge transformation.

The preparation tool fingerprints these graphs if supplied. It does **not** parse the ONNX graph or declare Unity compatibility.

## Runtime correctness gates

A real Quest translation adapter must pass these gates in order:

1. **Snapshot identity** — full upstream revision and all source artifact hashes fixed.
2. **Tokenizer parity** — PhraseLayer SentencePiece output matches a trusted Transformers/Marian reference on an English fixture corpus, including punctuation, apostrophes, Unicode normalization, numbers, and out-of-vocabulary text.
3. **Encoder/decoder contract** — imported ONNX input/output names, dtypes, dimensions, cache tensors, and vocabulary dimension are measured rather than assumed.
4. **Generation parity** — greedy output first matches a trusted reference. Beam search is a later quality/performance choice, not a prerequisite for proving the model path.
5. **Unity import** — all graphs import under the pinned `com.unity.ai.inference@2.2.1` API surface.
6. **Quest execution** — real Quest 3 inference succeeds and records cold/warm latency, memory, frame impact, and thermal behavior.
7. **Translation quality** — phrase-level fixtures relevant to signs, menus, instructions, labels, and ordinary prose are reviewed. Upstream benchmark scores alone do not establish PhraseLayer product quality.

## Current status

Implemented:

- stable `ITranslationEngine` compatibility;
- offline seq2seq orchestration contracts;
- strict Marian candidate metadata contract;
- cancellation and generation diagnostics;
- local snapshot/ONNX fingerprint tooling;
- deterministic Core and Python fixtures.

Still required:

- full 40-character upstream revision and revision-level artifact hash capture;
- SentencePiece runtime with parity fixtures;
- Unity Inference Marian encoder/decoder implementation;
- decoder cache/generation loop;
- real exported ONNX artifacts;
- Quest 3 performance and quality validation.

No model weights are bundled by this work.
