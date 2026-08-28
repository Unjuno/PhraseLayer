# Runtime dependency notes

## Microsoft.ML.Tokenizers 2.0.0

PhraseLayer pins `Microsoft.ML.Tokenizers` 2.0.0 as the managed SentencePiece implementation behind the optional `PhraseLayer.Tokenization.Microsoft` adapter.

The package is MIT licensed and exposes the required `SentencePieceTokenizer.Create(Stream)` path that parses exact SentencePiece ModelProto bytes, including Unigram/BPE model type and embedded normalization behavior.

A lower-dependency experiment with 1.0.3 was run in CI. Package restore succeeded, but adapter compilation failed because that version does not expose `SentencePieceTokenizer.Create`. PhraseLayer therefore rejects 1.0.3 for this adapter instead of replacing exact `.spm` parsing with an approximation.

The 2.0.0 .NET Standard dependency closure includes Google.Protobuf and modern `System.*` / `Microsoft.Bcl.*` support packages, including `System.Text.Json 9`. In the net8 integration-test graph this produced an assembly-version conflict warning against the platform `System.Text.Json 8` reference. The adapter still compiled successfully in the earlier 2.0.0 CI pass, but the warning is treated as a Unity/IL2CPP compatibility risk, not suppressed evidence.

Therefore:

- Core never references the package;
- the Unity assembly never references it at compile time;
- local runtime DLLs are git-ignored and staged only through `prepare_unity_tokenizer_runtime.py`;
- runtime staging fingerprints every copied DLL;
- real Unity 6000.0.66f2 import is required before the dependency closure is accepted for the product build;
- Android IL2CPP and Quest 3 execution remain separate gates;
- replacing this adapter with another exact SentencePiece implementation remains supported by the Core interface.

The dedicated NMT path remains a small seq2seq translation stack. This dependency is a deterministic tokenizer implementation, not an LLM runtime.
