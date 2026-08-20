# PhraseLayer

> Understand more. Translate less.

PhraseLayer is an open-source, offline-first mixed-reality language-learning project. It recognizes real-world text or speech and selectively supports semantic units with contextual translations. Learner comprehension is latent: PhraseLayer maintains an adaptive belief from action-aware evidence rather than claiming to directly know whether a learner understood something.

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

The visible mix may change across encounters as the learner belief changes. It stays frozen for the lifetime of the same encounter.

## Runtime contract

The official reference runtime is **local-only**:

- no PhraseLayer server or account;
- no automatic cloud inference fallback;
- learner state remains local;
- OCR, ASR, and translation stay behind replaceable interfaces;
- the official Quest build is guarded against accidental runtime networking permissions/APIs.

See `docs/LOCAL_ONLY.md`.

## Current implementation

Implemented in the hardware-independent core:

- semantic spans for words, multiword expressions, clauses, and sentences;
- longest-match multiword-expression detection;
- adaptive assistance density (Auto / Easy / Balanced / Challenge / Immersion);
- clause-first assistance and non-overlapping span selection;
- in-place mixed-language rendering without gloss markers;
- frozen encounter plans;
- action-aware learner observations and local learner-profile persistence;
- passive exposure / silence / generic completion do **not** create mastery evidence;
- OCR region → source-text alignment and semantic-unit → viewport-region alignment;
- downstream `ReadObservationPipeline` from an existing OCR observation to spatial assistance;
- replaceable OCR, ASR, and translation interfaces;
- deterministic Core tests and CI validators.

Implemented in the Unity / Quest shell:

- Unity 6000.0.66f2 project and Unity Build Automation gate;
- Meta passthrough-camera bridge and Android camera permission boundary;
- PP-OCRv6 Tiny Unity Inference detector/recognizer/crop/runtime scaffolding;
- verified model/dictionary staging contracts without committing model weights;
- passthrough-camera → OCR device smoke-test harness;
- OCR observation/frame handoff into a viewport-aligned Read-assistance debug slice;
- local-only Unity build guard.

## Still requiring device/runtime validation

The repository does **not** yet claim a Quest 3 end-to-end PASS. Remaining gates include:

- resolve and compile the current Unity/Meta project in cloud CI;
- configure and run the real Meta `PassthroughCameraAccess` component on Quest 3;
- stage the reviewed local PP-OCR assets and run real device OCR;
- replace the small debug translation dictionary with a reviewed local English→Japanese NMT runtime;
- stabilize encounter identity/spatial placement across camera frames;
- measure OCR/translation latency, XR frame time, memory, thermal behavior, and battery use on Quest 3;
- add the Listen/ASR path after the Read vertical slice is validated.

Hardware and learning-effect claims require measurements; scaffolding or synthetic tests are not treated as device evidence.

## CI

GitHub Core CI checks the platform-neutral core, learner-state invariants, local-only contract, OCR model/runtime contracts, Quest smoke-harness wiring, and the Unity shell compile fallback.

Unity Build Automation is the reference real-Unity cloud gate. See `docs/UNITY_BUILD_AUTOMATION.md`.

## License

Original PhraseLayer source is Apache-2.0. Third-party code, model weights, and datasets keep their own licenses. See `docs/LICENSING.md`.
