# OCR runtime boundary and scheduling

PhraseLayer treats camera capture rate, render rate, and OCR inference rate as separate concerns.

## Native Unity texture path

The Quest camera bridge returns `ImageFrame` with a `UnityTextureFramePayload`. `UnityTextureOcrEngine` adapts that frame to an `IUnityTextureOcrBackend`:

```text
Passthrough camera Texture
        ↓
UnityTextureFramePayload
        ↓
UnityTextureOcrEngine
        ↓
IUnityTextureOcrBackend
        ↓
PP-OCR / another Unity OCR implementation
        ↓
OcrObservation
```

A concrete backend may use Unity AI Inference, another GPU runtime, or an explicit CPU readback. The camera layer itself does not force a GPU→CPU copy.

`FixedUnityTextureOcrBackend` exists only for deterministic Editor/test wiring. It is not a production OCR model.

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

## Next production backend

The first production candidate remains PP-OCRv6 Tiny detection + recognition. Before binding it to `IUnityTextureOcrBackend`, verify the exact Unity AI Inference 2.2.1 operator/API support and pin the exact model revisions/files/licenses. Do not bake an assumed tensor layout or post-processing contract into Core.
