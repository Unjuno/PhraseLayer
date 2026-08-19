# PhraseLayer

> Understand more. Translate less.

PhraseLayer is an open-source, offline-first mixed-reality language-learning project. It recognizes real-world text or speech, estimates which semantic units a learner is likely to understand, and replaces only the units that need support with contextual translations.

The initial target is **English → Japanese on Meta Quest 3**. The core is device-independent so OCR, ASR, translation models, and XR front ends can be replaced without rewriting the learning logic.

## Core behavior

PhraseLayer does **not** translate an arbitrary percentage of characters and does **not** add brackets around translations. It selects complete semantic units and replaces those spans in place.

Source:

```text
I was tired, so I went home, and I fell asleep immediately.
```

Possible assisted view:

```text
I was tired, だから家に帰って, and I fell asleep immediately.
```

The visible mix changes across encounters as the learner model changes. It stays frozen while the learner is reading the same encounter.

## Current scope

Implemented in the hardware-independent core:

- semantic spans for words, multiword expressions, clauses, and sentences
- longest-match multiword-expression detection
- learner understanding scores
- adaptive assistance density (Auto / Easy / Balanced / Challenge / Immersion)
- clause-first assistance when a whole clause is difficult
- non-overlapping assistance selection
- in-place mixed-language rendering without markers
- encounter plan cache to prevent language flicker
- replaceable OCR, ASR, and translation interfaces
- fake Read/Listen pipelines for deterministic tests
- CI, model candidate manifest, and licensing/benchmark docs

Not implemented yet: Unity, Meta XR, Passthrough Camera, real OCR/ASR/NMT runtimes, spatial placement, or Quest benchmarks.

## License

Original PhraseLayer source is Apache-2.0. Third-party code, model weights, and datasets keep their own licenses. See `docs/LICENSING.md`.
