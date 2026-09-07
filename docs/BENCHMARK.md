# Benchmark protocol

Desktop results do not establish Quest 3 viability. Every real OCR, ASR, and translation adapter must record: headset/OS, Unity/Meta/runtime versions, model revision and quantization, backend/threads, cold start, p50/p95 steady latency, peak/steady PSS, XR frame time, CPU/GPU indicators, thermal behavior over 30 minutes, and battery change.

Measure stages separately:

```text
capture -> OCR -> segmentation/planning -> translation -> render
microphone -> VAD/ASR -> segmentation/planning -> translation -> subtitle
```

OCR fixtures should vary text size, distance, lighting, motion, perspective, and printed vs screen text. No device latency target is treated as verified before hardware measurement.

## Host-only deterministic experiments

The experiments below are regression/contract experiments. They intentionally do **not** establish Quest performance or human learning effectiveness.

### PP-OCR recognizer CTC transfer budget

`tools/measure_ppocr_ctc_transfer_budget.py` derives transfer payload from the pinned recognition dictionary and the production packed CTC ABI.

For the current pinned PP-OCRv6 Tiny recognizer:

- effective dictionary tokens: 6,905;
- CTC classes including blank: 6,906;
- full float32 probability payload: 6,906 values = 27,624 bytes per timestep;
- live packed payload: class index + max score = 2 float32 values = 8 bytes per timestep;
- payload reduction: **3,453x**;
- explicit recognizer output readbacks per crop: **1**.

This is a byte-count result only. It is not a claim of 3,453x latency improvement, GPU synchronization improvement, memory reduction, frame-rate improvement, or Quest 3 performance. Those require real Unity and device measurement.

### Packed CTC differential experiment

`PaddleCtcPackedDifferentialTests` compares the full probability-matrix greedy decoder with the packed `[class index, max score]` ABI across 1,000 deterministic random matrices. Exact maximum ties are deliberately injected and the comparison checks class indices, maximum scores, decoded text, emitted token count, and confidence. A separate fixture runs at the pinned 6,906-class scale and covers blank, highest dictionary index, first-index ties, adjacent duplicates, and blank-separated duplicates.

The real-Unity `PhraseLayerPaddleOcrRecognizerGpuReductionProbe` remains the stronger execution gate because it exercises Inference Engine GPU operators. Hosted differential tests do not substitute for that real-Unity run.

### Semantic assistance understanding sweep

`experiments/PhraseLayer.AssistanceSweep` runs the representative source

```text
I was tired, so I went home, and I fell asleep immediately.
```

through `RuleBasedSemanticSegmenter` + `AssistancePlanner` at 101 understanding values from 0.00 through 1.00 for Auto, Easy, Balanced, Challenge, and Immersion.

The experiment requires selected assistance to be monotonic non-increasing as understanding rises, verifies every replacement remains an exact semantic source span, and rejects character-percentage selection. At understanding 0.55 the current deterministic selected ratios are:

| mode | selected semantic-token ratio |
| --- | ---: |
| Easy | 0.75 |
| Balanced | 0.50 |
| Challenge | 0.25 |
| Immersion | 0.0833... |

During development this experiment exposed a numerical instability: clauses with identical learner understanding could receive tiny different difficulty values solely from floating-point weighted-average accumulation, changing which semantic spans were selected and causing assistance to increase on the next successful encounter. `AssistancePlanner` now quantizes only its difficulty **sorting key** to 12 decimal places before applying semantic-kind/source-order tie breakers; the underlying learner estimate remains unchanged.

### Repeated encounter adaptation experiment

The same experiment runner also applies the actual `LanguagePipeline`, `LearningEncounterSession`, and `LearnerAdaptationEngine` repeatedly from initial understanding 0.20 under Auto mode.

Two deterministic evidence trajectories are compared:

- **passive assisted exposure:** translated units receive only the normal `AssistedExposure` update while unassisted atoms receive successful-unassisted-completion evidence. After 80 encounters the selected assistance ratio remains `0.1666...` (2/12 semantic tokens). This is intentional with the current policy: passive exposure is capped below the `Known` threshold and therefore cannot silently mark every repeatedly translated unit as mastered.
- **successful recall on assisted units:** every assisted semantic unit receives `RecallSucceeded`, with successful-unassisted-completion evidence for the remaining atomic units. Assistance falls monotonically and reaches zero on encounter **13** for this deterministic fixture.

These trajectories describe the current adaptation algorithm, not observed human learning rates. The encounter counts must not be presented as a prediction of how many real exposures a learner needs.
