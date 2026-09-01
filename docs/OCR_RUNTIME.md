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

## PP-OCR detector input path

The reviewed Unity PP-OCR detector implementation is now bound to `com.unity.ai.inference@2.2.1`. Its camera-texture input path is:

```text
Meta Passthrough Camera Texture
        ↓
TextureConverter.ToTensor
  NCHW / TopLeft / BGRA swizzle
  detector resize on GPU
        ↓
FunctionalGraph
  (BGR - mean) / std
        ↓
PP-OCR detector Worker
        ↓
probability-map tensor
        ↓
ReadbackAndClone
        ↓
CPU DB post-processing
```

The live detector-input path must not use `Graphics.Blit`, `Texture2D.ReadPixels`, `GetPixels32`, or a temporary `RenderTexture`. Only the detector probability-map output is copied to CPU for the current DB decoder.

The reviewed normalization contract remains the Core PP-OCR contract:

- tensor layout: NCHW;
- channel order: BGR;
- row origin: top-left for the normal camera path;
- means: `0.485, 0.456, 0.406`;
- standard deviations: `0.229, 0.224, 0.225`;
- input values interpreted in `[0,1]` before normalization.

`UnityPaddleOcrDetectorRuntime.CreateReviewedTextureTransform` and `ApplyReviewedNormalization` are shared with the real-Unity parity probe so the verification path cannot silently use a different channel/origin/normalization contract.

## Real-Unity GPU preprocessing parity gate

`PhraseLayerPaddleOcrGpuPreprocessProbe` creates a 736×736 oriented RGB fixture and runs it through the same production texture transform and normalization helpers. Because the fixture already matches the PP-OCR detector limit side length, the parity comparison does not depend on resize interpolation.

The probe verifies sampled values for:

1. top-left versus bottom-left row orientation;
2. BGR channel order;
3. raw texture-to-tensor values;
4. the GPU FunctionalGraph mean/std result against `PaddleOcrV6TinyDetectionPreprocess.NormalizeChannel`.

The self-hosted `Quest 3 Read Mode Smoke` workflow runs this probe with a real graphics device before building the Android fixture. It intentionally does **not** pass `-nographics`.

This gate proves the reviewed preprocessing math and Unity texture/tensor semantics on that runner. It does **not** by itself prove that a Meta passthrough texture and its cached camera pose refer to the exact same physical exposure on Quest 3.

## Camera timestamp / pose claim boundary

The Meta camera bridge retains a stable `PassthroughCameraAccess.Timestamp` + `GetCameraPose()` pair with each accepted `ImageFrame`, and spatial projection reuses that captured pose for center and corner rays.

Removing detector-input CPU readback closes one known source of delay between frame capture and GPU submission. However, the Meta texture producer and GPU command execution remain asynchronous. Therefore device evidence must continue to report:

```text
camera_timestamp_pose_binding_implemented=true
camera_pixel_pose_sync_verified=false
```

until a real Quest 3 timing/visual gate demonstrates the stronger pixel↔pose synchronization claim. Do not infer that claim from Hosted CI or from the preprocessing parity probe.

## Recognizer path

The current recognizer/crop path still uses Unity graphics readback where required by the correctness-first implementation. That work occurs after detector geometry has been produced and therefore is separate from the camera-frame detector-input synchronization issue. It remains a performance optimization target and must not be described as end-to-end GPU-resident OCR.

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

## Quest smoke evidence privacy boundary

The device smoke runner uses process-scoped logcat only as an in-memory readiness source. Raw process logcat is not written to disk and is not uploaded as a workflow artifact. This is deliberate because real-world OCR may contain private text even when the current smoke behaviours redact their own recognized/display strings.

The only persisted text diagnostic is `quest-read-mode-diagnostics.txt`. Each candidate line must fully match one of the reviewed counter/status grammars in `SAFE_DIAGNOSTIC_PATTERNS`; matching a safe prefix is insufficient. A future change such as appending `recognized_text=...` to an otherwise valid counters line therefore causes that whole line to be discarded rather than partially preserved.

The persisted diagnostics are limited to smoke state, timings, counts, captured-pose counters, MRUK status/confidence, layout/mask/render counters, compact OCR stage, and the literal `FATAL EXCEPTION` marker. Fatal stack/message content is discarded. `recognized_text=` and `display_text=` are not valid diagnostic grammars.

ADB serials are also excluded from evidence. The JSON stores only a truncated SHA-256 fingerprint, and failure messages are scrubbed of the selected raw serial before serialization. The Quest workflow uploads explicit safe evidence files rather than a wildcard device-output directory.

This privacy boundary is part of the device gate contract, not a logging convention. Reintroducing raw logcat persistence, wildcard Quest output upload, or recognized/display text in the diagnostic grammar must fail Hosted validation before a device gate can be considered valid.

## Production status

PP-OCRv6 Tiny detection + recognition is no longer only an abstract candidate: the Unity adapter has a pinned Inference Engine 2.2.1 detector/recognizer implementation, local asset staging, model/dictionary contract probes, a guarded Hosted compile shell, and the real-Unity detector preprocessing parity gate described above.

Remaining production gates include real Quest 3 execution, imported-model parity on the target runtime, pixel↔pose timing evidence, visual quality/stereo comfort, and measured performance/thermal behavior. Model revisions/files/licenses remain pinned and reviewed separately; Core must not acquire Unity-specific tensor or graphics dependencies.
