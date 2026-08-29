# Managed SentencePiece runtime

PhraseLayer's offline English → Japanese translation path requires the exact tokenizer behavior encoded in the candidate Marian `source.spm` and `target.spm` files. Approximate whitespace tokenization or a hand-written Unicode normalizer is not acceptable because token drift changes model inputs and therefore translation output.

## Selected adapter boundary

Core remains independent of tokenizer libraries:

```text
PhraseLayer.Core
  ISentencePieceProcessor
  MarianSentencePieceTokenizer
          ↑
PhraseLayer.Tokenization.Microsoft
  MicrosoftMlSentencePieceProcessor
          ↓
Microsoft.ML.Tokenizers 2.0.0
```

`MicrosoftMlSentencePieceProcessor` loads the supplied SentencePiece protobuf bytes through `SentencePieceTokenizer.Create(Stream, addBeginningOfSentence: false, addEndOfSentence: false)`.

The Microsoft tokenizer parses the model type and embedded normalization rules from the `.spm` file. PhraseLayer consumes SentencePiece piece strings, then the existing Core `MarianSentencePieceTokenizer` maps those strings through Marian's external `vocab.json` before adding the Marian EOS token. SentencePiece internal ids are never assumed to equal Marian model ids.

## Exact Marian parity result

The managed adapter is compared directly against Hugging Face Transformers `MarianTokenizer` using the exact reviewed `Helsinki-NLP/opus-mt-en-jap` snapshot revision:

```text
a863894cdd2b80f3bc1c5966734aee9ffec207d1
```

The parity corpus contains 13 cases covering ordinary English, apostrophes, Unicode normalization, repeated whitespace, tabs/newlines, times, prices/symbols, non-breaking punctuation, and mixed English/Japanese input.

The first live comparison exposed a real implementation difference. Google's SentencePiece processor emits one surface token for a contiguous unknown region, whereas the Microsoft Unigram path emitted one unknown token per unmatched Unicode code point. For example, the exact Marian `source.spm` does not contain `0%`, `$9`, `99`, or `東京` as vocabulary entries, but Google SentencePiece returns each of those as one surface span carrying the UNK id.

PhraseLayer therefore coalesces only adjacent Microsoft surface-UNK tokens that carry the same internal token id. Known model pieces and separated unknown regions are unchanged. This behavior is regression-tested locally and against the exact upstream snapshot.

The resulting exact-snapshot comparison passes all 13 cases:

```text
PASS: Marian tokenizer parity cases=13
```

This is tokenizer parity evidence only. It is not evidence that the managed dependency closure imports successfully into Unity/IL2CPP or runs correctly on Quest 3.

## One-command local gate

The authoritative developer-machine entry point is:

```text
python tools/run_marian_tokenizer_parity_local.py
```

It performs the same steps used by hosted CI:

1. creates an isolated Python environment and installs the pinned reference toolchain;
2. reads the exact full Marian revision from `models/models.lock.json`;
3. stages the small metadata/tokenizer snapshot, or accepts `--snapshot-dir` for an existing exact snapshot;
4. verifies every staged artifact against the committed SHA-256/size evidence;
5. inspects `source.spm` with Google's SentencePiece implementation;
6. generates a Transformers `MarianTokenizer` reference fixture offline from the staged snapshot;
7. runs the managed C# parity comparison.

Generated evidence is written under the git-ignored directory:

```text
artifacts/local-marian-tokenizer-parity/
```

GitHub-hosted Actions invokes this same script rather than maintaining a separate CI-only implementation. Hosted CI is therefore a reproducibility check for the local procedure, not a substitute for the local Unity gate.

## Required local Unity gate

Before claiming Unity compatibility, run the same local gate with the real pinned editor:

```text
python tools/run_marian_tokenizer_parity_local.py \
  --require-unity \
  --unity-editor <UNITY_6000.0.66f2_EDITOR_EXECUTABLE>
```

Alternatively set `UNITY_EDITOR` and use `--require-unity`.

The strict Unity path additionally:

1. restores and builds `PhraseLayer.Tokenization.Microsoft`;
2. stages the managed tokenizer dependency closure into the Unity project;
3. stages the exact Marian tokenizer assets;
4. launches the real Unity Editor in batch mode;
5. executes `PhraseLayer.Unity.Editor.PhraseLayerEditorVerification.VerifyCorePipelineBatch`.

If Unity is required but cannot be launched, the local gate fails. A successful hosted `.NET` build does not satisfy this requirement.

The manual `Unity CLI` workflow uses a self-hosted runner labelled for Unity 6000.0.66f2 and invokes this same strict local gate. It exists as an additional controlled-machine check, not as evidence until that workflow has actually run successfully.

## Local Unity staging details

No tokenizer DLLs or Marian assets are committed by default.

The runtime staging command used by the local gate is equivalent to:

```text
python tools/prepare_unity_tokenizer_runtime.py \
  --build-output src/PhraseLayer.Tokenization.Microsoft/bin/Release/netstandard2.1
```

It copies the managed dependency closure present beside the adapter into:

```text
unity/PhraseLayer.Unity/Assets/LocalTokenizerRuntime/
```

It deliberately excludes `PhraseLayer.Core.dll`, because Unity already consumes Core through the local `com.unjuno.phraselayer.core` package. The staging tool deletes previously staged DLLs first so a package upgrade cannot leave stale dependency binaries behind. It also produces a SHA-256 evidence manifest under the ignored `artifacts/` directory.

`Microsoft.ML.Tokenizers 1.0.3` was tested as a lower-dependency alternative, but it does not expose the required `SentencePieceTokenizer.Create(Stream)` API. `Microsoft.ML.Tokenizers 3.0.0-preview.26160.2` was also tested during live parity investigation and produced the same per-code-point unknown-span difference, so PhraseLayer remains on stable 2.0.0 rather than adopting a preview without benefit.

The 2.0.0 dependency closure includes `System.Text.Json 9` and produces a version-conflict warning in the net8 integration-test graph. That remains a real Unity/IL2CPP import risk until the strict local Unity gate passes.

## Local Marian tokenizer assets

For a revision-pinned local `Helsinki-NLP/opus-mt-en-jap` snapshot:

```text
python tools/prepare_unity_marian_tokenizer_assets.py \
  --snapshot-dir <LOCAL_REVISION_PINNED_SNAPSHOT> \
  --revision <FULL_40_CHARACTER_SHA>
```

The tool requires:

- a full lowercase 40-character upstream revision;
- exactly 46,276 `vocab.json` entries;
- unique ids covering `0..46275`;
- non-empty `source.spm` and `target.spm`.

It stages only:

```text
Assets/Resources/LocalTranslationAssets/source.spm.bytes
Assets/Resources/LocalTranslationAssets/target.spm.bytes
Assets/Resources/LocalTranslationAssets/vocab.json
```

No model weights are copied by this tokenizer staging step.

Unity then resolves the resource root `LocalTranslationAssets` and passes the exact bytes/text to `UnityManagedMarianTokenizerLoader`.

## Validation status

Completed:

1. synthetic SentencePiece Unigram parsing with the real Microsoft library;
2. embedded normalization and piece round-trip tests;
3. Google-compatible contiguous unknown-span grouping regression tests;
4. Marian external-vocabulary mapping tests;
5. exact 13-case Transformers parity against revision `a863894cdd2b80f3bc1c5966734aee9ffec207d1`;
6. exact snapshot SHA-256/size verification;
7. deterministic managed runtime staging tests;
8. deterministic Marian tokenizer asset staging tests;
9. Unity shell compilation of the late-bound loader contract.

Still required before claiming Quest compatibility:

1. successful strict local Unity 6000.0.66f2 Editor import/batch verification on a machine with the pinned editor installed;
2. Android/IL2CPP build validation;
3. Quest 3 runtime tokenization test;
4. real Marian ONNX encoder/decoder execution through Unity Inference;
5. memory, latency, thermal, and allocation measurements on Quest 3;
6. revision-level redistribution review before shipping model artifacts.

This work does not use an LLM at runtime. SentencePiece is the deterministic tokenizer layer for a dedicated Marian seq2seq translation model.
