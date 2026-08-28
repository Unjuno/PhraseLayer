# Implementation status — 2026-08-29

Current SentencePiece/NMT implementation state:

- dedicated Marian seq2seq translation contracts are on `main`;
- no LLM is required or integrated for Read Mode translation;
- `Microsoft.ML.Tokenizers 2.0.0` is wrapped behind a separate `PhraseLayer.Tokenization.Microsoft` adapter;
- the adapter consumes exact SentencePiece `.spm` bytes and returns piece strings through the Core `ISentencePieceProcessor` contract;
- Marian external vocabulary mapping and EOS policy remain in Core;
- Unity loads the optional managed tokenizer runtime through reflection rather than a compile-time dependency;
- local runtime DLLs and tokenizer assets are staged through deterministic Python tools and remain git-ignored;
- container fixture validation covers the Python staging tools;
- GitHub Actions is responsible for real NuGet restore, C# build/tests, and Unity shell compilation;
- real Unity Editor import, Android IL2CPP, Quest execution, tokenizer parity against Transformers, and actual Marian graph execution remain unverified.
