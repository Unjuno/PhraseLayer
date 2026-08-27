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

## Current implementation

Implemented and host-tested:

- semantic spans for words, multiword expressions, clauses, and sentences
- longest-match multiword-expression detection
- learner understanding scores and persisted learner profiles
- adaptive assistance density (Auto / Easy / Balanced / Challenge / Immersion)
- clause-first assistance when a whole clause is difficult
- non-overlapping assistance selection
- in-place mixed-language planning without gloss markers
- encounter plan stability plus deferred learner updates for later encounters
- replaceable OCR, ASR, translation, camera, and XR-facing interfaces
- fake Read/Listen pipelines for deterministic tests
- Unity 6 shell and reproducible demo-scene setup
- Meta Passthrough Camera bridge kept outside `PhraseLayer.Core`
- PP-OCR detector → DB quad decode → perspective crop → recognizer → CTC runtime path using Unity Inference Engine
- single-observation Read Mode handoff so OCR results are reused rather than inferred twice
- OCR image/viewport geometry and semantic-to-OCR-region alignment
- conservative viewport-ray → Unity surface projection
- four-corner physical text-plane fitting with metric extent/planarity gates
- temporal world-text track association, smoothing, and short dropout retention
- font-injected non-destructive Unity world-space translated-text renderer
- synchronization-context guards for Unity/main-thread-bound platform adapters
- CI, model candidate manifest, licensing, staging, and benchmark contracts

Implemented but **not yet claimed as Quest-verified**:

- automatic demo-scene wiring for Meta `PassthroughCameraAccess` → camera bridge → OCR runtime → PP-OCR bootstrap
- semantic OCR geometry → four-corner surface fit → temporal world-text tracking
- world-space translated text rendering after a reviewed Japanese-capable Unity `Font` is assigned
- pinned PP-OCR model/dictionary asset preparation and Unity runtime integration

Still required for the first complete Read Mode:

- real Quest 3 camera/OCR/surface-registration execution and device measurements
- review and assignment of a Japanese-capable font asset for release packaging
- source-text masking/occlusion validated against neighboring physical content and stereo passthrough
- a reviewed offline English → Japanese NMT runtime (the current Core fallback is dictionary-based)
- camera hardware timestamp / pose/depth synchronization verification
- 30-minute Quest performance/thermal/battery validation

Listen Mode still requires microphone/VAD and a local ASR runtime.

## License

Original PhraseLayer source is Apache-2.0. Third-party code, model weights, fonts, and datasets keep their own licenses. See `docs/LICENSING.md`.
