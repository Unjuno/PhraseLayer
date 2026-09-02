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

The reviewed Unity PP-OCR detector implementation is bound to `com.unity.ai.inference@2.2.1`. Its camera-texture input path is:

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

## PP-OCR recognizer input path

The detector quad remains on the GPU while the crop and recognizer input are prepared:

```text
Camera texture + detected quad
        ↓
PaddleOcrPerspectiveCrop shader
  projective rectification on GPU
        ↓
rectified RenderTexture
        ↓
PaddleOcrRecognizerPreprocess shader
  aspect-preserving resize to height 48
  left-aligned valid image columns
  right normalized-zero padding
  encoded RGB recovery when project color space is Linear
  (x - 0.5) / 0.5 normalization
        ↓
TextureConverter.ToTensor
  NCHW / TopLeft / BGRA swizzle
        ↓
PP-OCR recognizer Worker
        ↓
[1,time,class] probability tensor
        ↓
ReadbackAndClone
        ↓
CPU CTC greedy decode
```

`UnityPaddleOcrRecognizerRuntime` must not call `Texture2D.ReadPixels`, `GetPixels32`, or use `RenderTexture.active` for input preprocessing. `Graphics.Blit` is still used deliberately for the GPU perspective crop and recognizer preprocessing shader; that is GPU-to-GPU work and is not an image readback.

The recognizer contract remains the Core `PaddleOcrV6TinyRecognitionPreprocess` contract:

- tensor layout: NCHW;
- channel order: BGR;
- row origin: top-left;
- model height: 48;
- default model width: 320;
- image resize preserves aspect ratio and uses `ceil(height * sourceWidth / sourceHeight)`, capped to model width;
- normalized image value: `(byte / 255 - 0.5) / 0.5`;
- unused right columns are **normalized zero**, matching PaddleOCR's zero-initialized tensor.

`UnityPaddleOcrRecognizerRuntime.CreateReviewedPreprocessMaterial`, `CreateReviewedTextureTransform`, and `PopulateReviewedInputTensor` are production helpers reused by the real-Unity parity probe. This prevents the probe from validating a parallel implementation that the runtime does not execute.

## Real-Unity GPU preprocessing parity gates

Detector parity uses `PhraseLayerPaddleOcrGpuPreprocessProbe`. It creates a 736×736 oriented RGB fixture and runs it through the same production detector texture transform and normalization helpers. Because the fixture already matches the PP-OCR detector limit side length, the comparison does not depend on resize interpolation.

The detector probe verifies sampled values for:

1. top-left versus bottom-left row orientation;
2. BGR channel order;
3. raw texture-to-tensor values;
4. the GPU FunctionalGraph mean/std result against `PaddleOcrV6TinyDetectionPreprocess.NormalizeChannel`.

Recognizer parity uses `PhraseLayerPaddleOcrRecognizerGpuPreprocessProbe`. Its fixture is 64×48 into a 96×48 model tensor. Height already matches the recognizer model, so `ResizedWidth` is exactly 64: the left 64 columns are one-to-one pixel-center samples and the right 32 columns are padding. This intentionally removes resize interpolation from the numerical comparison.

The recognizer probe verifies:

1. top-left row orientation;
2. BGR channel order;
3. `(x-0.5)/0.5` normalization against `PaddleOcrV6TinyRecognitionPreprocess.NormalizeChannel`;
4. exact normalized-zero values in sampled right-padding columns;
5. the same production shader/material and `PopulateReviewedInputTensor` helper used by runtime inference.

Self-hosted Read Mode gates run both preprocessing probes with a real graphics device before packaging. Neither parity runner may use `-nographics`.

These gates prove the reviewed preprocessing math and Unity texture/tensor semantics on that runner. They do **not** by themselves prove that a Meta passthrough texture and its cached camera pose refer to the exact same physical exposure on Quest 3.

## Camera timestamp / pose claim boundary

The Meta camera bridge retains a stable `PassthroughCameraAccess.Timestamp` + `GetCameraPose()` pair with each accepted `ImageFrame`, and spatial projection reuses that captured pose for center and corner rays.

Removing detector and recognizer input CPU image readback closes known avoidable stalls between camera capture, detector submission, crop preparation, and recognition submission. However, the Meta texture producer and GPU command execution remain asynchronous. Therefore device evidence must continue to report:

```text
camera_timestamp_pose_binding_implemented=true
camera_pixel_pose_sync_verified=false
```

until a real Quest 3 timing/visual gate demonstrates the stronger pixel↔pose synchronization claim. Do not infer that claim from Hosted CI or from either preprocessing parity probe.

## GPU-residency claim boundary

The OCR image path no longer performs CPU image readback between camera texture, detector input, perspective crop, and recognizer input. This does **not** mean the entire OCR algorithm is GPU-resident:

- detector probability maps are copied to CPU for DB post-processing and quad generation;
- recognizer probability matrices are copied to CPU for CTC greedy decoding;
- OCR observation assembly and semantic alignment remain CPU/Core work.

Accordingly, documentation and evidence may say **detector and recognizer image preprocessing are GPU-side**. They must not claim end-to-end GPU-resident OCR unless DB post-processing/CTC decoding are redesigned and separately validated.

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

PP-OCRv6 Tiny detection + recognition is no longer only an abstract candidate: the Unity adapter has a pinned Inference Engine 2.2.1 detector/recognizer implementation, local asset staging, model/dictionary contract probes, guarded Hosted compile coverage for the detector and recognizer GPU preprocessing paths, and separate real-Unity numerical parity gates for detector and recognizer preprocessing.

Remaining production gates include real Quest 3 execution, imported-model parity on the target runtime, pixel↔pose timing evidence, visual quality/stereo comfort, and measured performance/thermal behavior. Model revisions/files/licenses remain pinned and reviewed separately; Core must not acquire Unity-specific tensor or graphics dependencies.
