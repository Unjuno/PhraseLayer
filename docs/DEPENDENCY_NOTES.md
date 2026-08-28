# Runtime dependency notes

## Microsoft.ML.Tokenizers 2.0.0

PhraseLayer currently evaluates `Microsoft.ML.Tokenizers` 2.0.0 as the managed SentencePiece implementation behind the optional `PhraseLayer.Tokenization.Microsoft` adapter.

The package is MIT licensed and supports .NET Standard 2.0, but its .NET Standard dependency closure includes Google.Protobuf and several modern `System.*` / `Microsoft.Bcl.*` support packages. Ordinary .NET compatibility does not prove that Unity 6000.0.66f2 and Android IL2CPP can import that exact closure safely.

Therefore:

- Core never references the package;
- the Unity assembly never references it at compile time;
- local runtime DLLs are git-ignored and staged only through `prepare_unity_tokenizer_runtime.py`;
- runtime staging fingerprints every copied DLL;
- no Quest compatibility claim is made until real Unity import, Android build, and Quest execution pass;
- replacing this adapter with another exact SentencePiece implementation remains supported by the Core interface.

The dedicated NMT path remains a small seq2seq translation stack. This dependency is a deterministic tokenizer implementation, not an LLM runtime.
