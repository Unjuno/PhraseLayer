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

The Microsoft tokenizer parses the model type and embedded normalization rules from the `.spm` file. PhraseLayer consumes `EncodedToken.Value` as SentencePiece piece strings, then the existing Core `MarianSentencePieceTokenizer` maps those piece strings through Marian's external `vocab.json` before adding the Marian EOS token. SentencePiece internal ids are never assumed to equal Marian model ids.

Unknown surface text is also handled at this boundary deliberately. A SentencePiece implementation may return surface pieces that are not literal `<unk>` strings. Core therefore maps any piece absent from Marian's external `vocab.json` to the configured Marian `<unk>` id. Tests verify the external-vocabulary result rather than relying on one library's spelling of an unknown surface piece.

## Why the adapter is separate from Core

`PhraseLayer.Core` must not depend on a concrete tokenizer/runtime package. The Microsoft adapter is a separate .NET Standard assembly, so another reviewed implementation can replace it without changing assistance planning, learner adaptation, OCR, spatial alignment, or translation orchestration.

The Unity assembly also does not take a compile-time dependency on `Microsoft.ML.Tokenizers` or `PhraseLayer.Tokenization.Microsoft`. `UnityManagedMarianTokenizerLoader` discovers the optional adapter assembly by reflection. If the local runtime is absent or has contract drift, tokenizer creation fails explicitly rather than falling back to an approximate tokenizer.

## Local Unity staging

No tokenizer DLLs or Marian assets are committed by default.

Build the adapter locally, then stage the managed build output:

```text
python tools/prepare_unity_tokenizer_runtime.py \
  --build-output src/PhraseLayer.Tokenization.Microsoft/bin/Release/netstandard2.1
```

The tool copies the managed dependency closure present beside the adapter into:

```text
unity/PhraseLayer.Unity/Assets/LocalTokenizerRuntime/
```

It deliberately excludes `PhraseLayer.Core.dll`, because Unity already consumes Core through the local `com.unjuno.phraselayer.core` package. The staging tool deletes previously staged DLLs first so a package upgrade cannot leave stale dependency binaries behind. It also produces a SHA-256 evidence manifest under the ignored `artifacts/` directory.

`Microsoft.ML.Tokenizers 1.0.3` was tested as a lower-dependency alternative, but CI proved that it does not expose the required `SentencePieceTokenizer.Create(Stream)` API. PhraseLayer therefore keeps 2.0.0 rather than substituting an approximate tokenizer. The 2.0.0 dependency closure includes `System.Text.Json 9` and produced a version-conflict warning in the net8 integration-test graph; that is tracked as a real Unity/IL2CPP import risk rather than hidden or suppressed.

The managed dependency closure must still pass a real Unity 6000.0.66f2 import test. Ordinary .NET CI success is not sufficient evidence of Unity/IL2CPP compatibility.

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

## Validation gates

Implemented automated gates:

1. synthetic SentencePiece Unigram ModelProto is parsed by the real `Microsoft.ML.Tokenizers` library;
2. embedded whitespace normalization and piece round-trip are checked;
3. unknown surface pieces are checked at the Marian external-vocabulary mapping boundary;
4. external Marian vocabulary mapping remains in Core;
5. managed runtime staging requires the adapter, Microsoft tokenizer, and Google.Protobuf assemblies;
6. stale staged DLLs are deleted;
7. Marian tokenizer asset staging rejects short revisions and malformed external vocabulary ids;
8. Unity shell compiles the late-bound loader contract.

Still required before claiming Quest compatibility:

1. trusted Hugging Face/Transformers parity fixtures using the exact candidate `source.spm`, `target.spm`, and `vocab.json`;
2. real Unity 6000.0.66f2 import of the staged managed dependency closure;
3. Android/IL2CPP build validation;
4. Quest 3 runtime tokenization test;
5. memory, latency, and allocation measurements;
6. revision-level redistribution review and final upstream artifact hashes.

This work does not use an LLM. SentencePiece is only the deterministic tokenizer layer for a dedicated Marian seq2seq translation model.
