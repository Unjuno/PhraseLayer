# OCR runtime boundary and scheduling

PhraseLayer treats camera capture rate, render rate, and OCR inference rate as separate concerns.

## Native Unity texture path

The Quest camera bridge returns `ImageFrame` with a `UnityTextureFramePayload`. The reviewed PP-OCR implementation consumes that texture through the local Unity AI Inference path:

```text
Passthrough camera Texture
        ↓
UnityTextureFramePayload
        ↓
OcrRuntimePump / OcrFrameScheduler
        ↓
UnityPaddleOcrEngine
        ↓
PP-OCR detector + recognizer
        ↓
OcrObservation
```

The camera layer itself does not force a network request or remote OCR fallback. Model execution remains behind `IOcrEngine`, so another local implementation can replace PP-OCR without changing camera capture, scheduling, semantic alignment, or presentation.

`FixedUnityTextureOcrBackend` exists only for deterministic Editor/test wiring. It is not a production OCR model.

## Local PP-OCR Player startup

Model weights are intentionally not committed. `tools/prepare_unity_ocr_assets.py` stages the reviewed local bundle below the git-ignored `Assets/LocalOcrAssets/PaddleOCR` tree:

- `detector.onnx`
- `recognizer.onnx`
- `ppocr_keys.txt`
- `ppocr_keys.manifest.json`

Before a Player build, `PhraseLayerLocalOcrRuntimeConfigBuildHook` applies an all-or-none policy:

1. if none of the four local files exists, the build remains valid and the committed Read MVP uses its deterministic synthetic OCR fixture;
2. if only part of the bundle exists, the build fails before export rather than silently producing an ambiguous OCR mode;
3. if the complete bundle exists, the Editor verifies the imported models and dictionary manifest and generates `Assets/LocalOcrAssets/PaddleOCR/Resources/PhraseLayerLocalOcrRuntimeConfig.asset`.

The generated config remains inside the git-ignored local asset tree, but Unity includes it and the referenced local model assets in that developer's Player build through `Resources`. No model URL, downloader, or remote fallback is added.

At runtime, `PhraseLayerReadMvpRuntimeInstaller` constructs its root inactive, loads `UnityLocalOcrRuntimeConfig`, injects the references into `UnityPaddleOcrBootstrapBehaviour`, and only then activates the root. A complete config therefore starts camera PP-OCR automatically; an absent/incomplete config keeps the synthetic fixture. The runtime log reports either `OCR=local-ppocr-camera` or `OCR=synthetic-fixture` so the active mode is explicit.

This removes the previous requirement to regenerate the Read scene merely to switch from the committed synthetic fixture to already-staged local OCR assets.

## Scheduling / backpressure

`OcrFrameScheduler` wraps any `IOcrEngine` and enforces three rules:

1. at most one inference is active;
2. frames older than the last processed frame are rejected;
3. accepted frames are rate-limited by source timestamp.

When a new camera frame arrives while OCR is still active, the frame is returned as `SkippedBusy`; it is not queued. This prevents a backlog where OCR results describe what the user looked at several seconds ago.

Example policy for initial Quest benchmarking:

```text
XR rendering: device target refresh rate
camera capture: platform cadence
OCR ceiling: start around 5 Hz, then benchmark
tracking/render reuse: every render frame
translation: only when a new semantic target needs translation
```

The 5 Hz value is a starting hypothesis, not a measured Quest 3 result. `OcrFrameScheduler` accepts any positive maximum inference rate so the actual value can be selected from device measurements.

## Schedule outcomes

- `Processed`: OCR ran and produced an observation.
- `SkippedBusy`: an inference was already active.
- `SkippedRateLimit`: the frame arrived before the configured inference interval.
- `SkippedStale`: the frame timestamp was not newer than the last successfully processed frame.

A failed OCR inference does not advance the last-processed timestamp, so a later frame can retry immediately. `Reset()` starts a new timestamp sequence, for example after restarting the camera stream.

## Remaining device gates

The code path and import/runtime contracts are implemented, but real Quest 3 evidence is still required for:

- Passthrough Camera permission/startup and actual texture capture;
- detector/recognizer execution on the staged model files in an Android ARM64 IL2CPP build;
- OCR accuracy on real Quest camera frames;
- sustained OCR cadence, GPU/CPU cost, memory, thermal behavior and battery impact;
- final selection of the OCR scheduling rate from measurements rather than the current 5 Hz starting value.

Do not claim those device results from host compile or synthetic inference alone.
