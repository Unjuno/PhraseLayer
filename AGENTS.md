# AGENTS.md

## Product invariants

1. Never implement character-percentage translation.
2. Core output must not add brackets, arrows, parentheses, or gloss markers.
3. Assistance operates on semantic spans (word, MWE, phrase, clause, sentence).
4. Prefer preserving source text when estimated understanding is high.
5. Freeze an assistance plan for the lifetime of an encounter; recompute on a later encounter.
6. OCR, ASR, translation, and XR runtimes remain replaceable behind interfaces.
7. `PhraseLayer.Core` must not depend on UnityEngine, Meta XR, Oculus, Android, networking SDKs, or model runtimes.
8. No cloud dependency is mandatory for the core path.
9. Do not commit model weights before redistribution licenses are reviewed and recorded.
10. Hardware performance claims require Quest 3 measurements.

## Current gate

Gate 0–2 only: repository foundation, adaptive language core, and fake input pipelines. Do not add Unity/Meta dependencies until pure-core CI is green.

## Required tests for assistance changes

- at least one multiword expression;
- at least one clause-level replacement;
- no overlapping selected spans;
- punctuation and whitespace outside replaced spans are preserved;
- a known expression loses automatic assistance;
- a cached encounter does not flicker after learner state changes.
