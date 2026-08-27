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
      ↓                         ↓
MarianSentencePiece      GreedySeq2SeqTranslationModel
Tokenizer                       ↓
      ↓                  ISeq2SeqGenerationBackend
ISentencePieceProcessor          ↓
                         platform encoder/decoder + KV cache
```

`OfflineSeq2SeqTranslationRuntime` translates the exact semantic span requested by `LanguagePipeline`. The surrounding sentence remains available as `OfflineTranslationRequest.Context`, but the baseline runtime does not silently concatenate context into the model input because doing so would change the span being translated.

The SentencePiece processor and model backend remain separate contracts so normalization/segmentation and Unity Inference encoder/decoder execution can be validated independently.

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

## Marian tokenizer boundary

`MarianSentencePieceTokenizer` implements the model-facing `ITranslationTokenizer` contract without pretending to implement SentencePiece itself.

The critical mapping is:

```text
source text
  ↓ exact source.spm processor
SentencePiece piece strings
  ↓ external vocab.json lookup
Marian model token ids
  ↓ reserve one slot + append EOS
encoder input ids
```

SentencePiece internal piece ids are deliberately **not** treated as Marian vocabulary ids. Unknown source pieces map to the external `<unk>` id. Source truncation reserves the final allowed input slot for EOS.

Target decoding reverses the external vocabulary mapping, removes generation-only EOS/PAD (and optional `<eop>` / `<eod>`) tokens, and then delegates piece reconstruction to the exact `target.spm` processor.

What remains is an `ISentencePieceProcessor` implementation that can execute the normalization and segmentation encoded in the exact `.spm` model and pass parity fixtures against a trusted MarianTokenizer reference. PhraseLayer must not replace that with whitespace splitting or an approximate Unicode normalizer.

## Correctness-first generation

`GreedySeq2SeqTranslationModel` now owns the platform-neutral generation loop:

```text
source ids
  ↓
ISeq2SeqGenerationBackend.StartAsync
  ↓ encoder once
ISeq2SeqGenerationSession
  ↓ decoder_start_token
DecodeNextAsync
  ↓ logits[46,276]
finite/shape validation → banned-token filter → argmax
  ↓ selected token
DecodeNextAsync again using backend-owned KV cache
  ↓
EOS or maximum target tokens
```

The backend owns encoder output and decoder past-key/value tensors. This keeps Unity tensor/runtime details outside Core while making token-selection behavior deterministic and testable.

The baseline intentionally supports `beamWidth=1` only. The upstream configuration uses four beams, but PhraseLayer does not silently label greedy decoding as beam search. Beam search is a separate quality/performance implementation gate.

At the maximum target length, the baseline can force EOS into the final slot rather than returning an unterminated sequence. Decoder vocabulary-size drift and non-finite logits fail loudly.

## Reproducible local preparation

`tools/prepare_marian_translation.py` intentionally performs no network download. It consumes a locally available, revision-pinned upstream snapshot and validates:

- `config.json`;
- `generation_config.json`;
- `tokenizer_config.json`;
- `vocab.json` contains exactly 46,276 unique integer ids spanning 0..46,275;
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
2. **SentencePiece parity** — `ISentencePieceProcessor` output matches a trusted Transformers/Marian reference on an English fixture corpus, including punctuation, apostrophes, Unicode normalization, numbers, whitespace, and out-of-vocabulary text. External-vocabulary mapping is already covered separately in Core tests.
3. **Encoder/decoder contract** — imported ONNX input/output names, dtypes, dimensions, cache tensors, and vocabulary dimension are measured rather than assumed.
4. **Greedy generation parity** — Unity backend output first matches the trusted reference under greedy decoding. The Core argmax/cache-driving loop is already deterministic; graph execution parity remains.
5. **Beam-search decision** — compare greedy quality/latency against the upstream four-beam configuration before deciding whether beam search is required on Quest.
6. **Unity import** — all graphs import under the pinned `com.unity.ai.inference@2.2.1` API surface.
7. **Quest execution** — real Quest 3 inference succeeds and records cold/warm latency, memory, frame impact, and thermal behavior.
8. **Translation quality** — phrase-level fixtures relevant to signs, menus, instructions, labels, and ordinary prose are reviewed. Upstream benchmark scores alone do not establish PhraseLayer product quality.

## Current status

Implemented:

- stable `ITranslationEngine` compatibility;
- offline seq2seq orchestration contracts;
- strict Marian candidate metadata contract;
- Marian SentencePiece-piece ↔ external-vocabulary mapping, EOS insertion, truncation, and target reconstruction boundary;
- cache-friendly correctness-first greedy generation loop with shape/finite-value guards;
- cancellation and generation diagnostics;
- local snapshot/ONNX fingerprint tooling with unique contiguous vocabulary validation;
- deterministic Core and Python fixtures.

Still required:

- full 40-character upstream revision and revision-level artifact hash capture;
- actual `ISentencePieceProcessor` implementation plus reference parity fixtures;
- real ONNX export and measured encoder/decoder/cache tensor contract;
- Unity Inference `ISeq2SeqGenerationBackend` implementation;
- optional beam search if quality evidence justifies its runtime cost;
- Quest 3 performance and translation-quality validation.

No model weights are bundled by this work.
