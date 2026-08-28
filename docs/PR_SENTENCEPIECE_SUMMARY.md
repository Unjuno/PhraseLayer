# Managed SentencePiece slice summary

This branch converts the offline Marian tokenizer boundary from an interface-only placeholder into a concrete, replaceable managed implementation while preserving PhraseLayer's Core/runtime separation.

Key properties:

- exact `.spm` parsing through `Microsoft.ML.Tokenizers` rather than approximate tokenization;
- SentencePiece piece strings remain distinct from Marian external vocabulary ids;
- Unity runtime discovery is late-bound and fails loudly when local managed assets are absent;
- tokenizer/runtime assets remain local and git-ignored;
- no translation weights are bundled;
- no LLM is introduced;
- Python staging tools were exercised in the available container;
- C# and Unity shell validation are enforced in GitHub Actions.
