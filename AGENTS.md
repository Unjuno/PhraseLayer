# AGENTS.md

## Product invariants

1. Never implement character-percentage translation.
2. Core output must not add brackets, arrows, parentheses, or gloss markers.
3. Assistance operates on semantic spans (word, MWE, phrase, clause, sentence).
4. Prefer preserving source text when estimated unaided-processing belief is high.
5. Freeze an assistance plan for the lifetime of an encounter; recompute on a later encounter.
6. OCR, ASR, translation, and XR runtimes remain replaceable behind interfaces.
7. `PhraseLayer.Core` must not depend on UnityEngine, Meta XR, Oculus, Android, networking SDKs, or model runtimes.
8. The official runtime is local-only: no PhraseLayer backend, telemetry SDK, remote inference provider, or automatic cloud fallback.
9. The official Quest build must not force/request Android `INTERNET` or `ACCESS_NETWORK_STATE` permissions.
10. Passive exposure, silence, or generic encounter completion must not be treated as learner-state evidence. State-changing evidence must record the action/origin that generated it.
11. Learning/forgetting transition prediction must remain separate from observation-driven learner-state updates.
12. Do not commit model weights before redistribution licenses are reviewed and recorded.
13. Hardware performance claims require Quest 3 measurements.

## Current gate

Gate 0–4 bootstrap: repository foundation, adaptive language core, fake input pipelines, Unity shell, and local OCR runtime scaffolding. Preserve pure-core CI and the local-only runtime contract while Quest camera/OCR validation continues.

## Required tests for assistance changes

- at least one multiword expression;
- at least one clause-level replacement;
- no overlapping selected spans;
- punctuation and whitespace outside replaced spans are preserved;
- a known expression loses automatic assistance;
- a cached encounter does not flicker after learner state changes;
- density changes must respect mode budgets and monotonicity regression fixtures.

## Required tests for learner-state changes

- passive assisted exposure does not mutate learner state;
- silent/generic completion does not mutate learner state;
- verified action-aware evidence records its observation origin;
- assisted silence is never treated as source-only success;
- a no-evidence event does not create a persisted explicit learner entry.
