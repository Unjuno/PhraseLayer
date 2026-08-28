# Runtime dependency notes

## Microsoft.ML.Tokenizers 1.0.3

PhraseLayer pins `Microsoft.ML.Tokenizers` 1.0.3 as the managed SentencePiece implementation behind the optional `PhraseLayer.Tokenization.Microsoft` adapter, subject to CI proving the required SentencePiece API and Unigram behavior.

The package is MIT licensed and supports .NET Standard 2.0. Its .NET Standard dependency closure is substantially smaller than 2.0.0 and is centered on:

- `Google.Protobuf >= 3.27.1`;
- `Microsoft.Bcl.HashCode >= 6.0.0`;
- `Microsoft.Bcl.Memory >= 9.0.0`;
- `System.Text.Json >= 8.0.5`.

The initially evaluated 2.0.0 package pulled `System.Text.Json 9` and other newer support packages into the validation graph. In the net8 integration-test build this produced an assembly conflict warning against the platform `System.Text.Json 8` reference. PhraseLayer therefore tests 1.0.3 as the lower-dependency pin instead of accepting that conflict by default.

Ordinary .NET compatibility still does not prove that Unity 6000.0.66f2 and Android IL2CPP can import the dependency closure safely.

Therefore:

- Core never references the package;
- the Unity assembly never references it at compile time;
- local runtime DLLs are git-ignored and staged only through `prepare_unity_tokenizer_runtime.py`;
- runtime staging fingerprints every copied DLL;
- no Quest compatibility claim is made until real Unity import, Android build, and Quest execution pass;
- replacing this adapter with another exact SentencePiece implementation remains supported by the Core interface.

The dedicated NMT path remains a small seq2seq translation stack. This dependency is a deterministic tokenizer implementation, not an LLM runtime.
