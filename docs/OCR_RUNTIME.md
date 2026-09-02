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

## PP-OCR recognizer input and CTC path

The detector quad remains on the GPU while the crop and recognizer input are prepared. The live path is:

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
[1,time,class] probability tensor on GPU
        ↓
Functional.ArgMax(class axis)
Functional.ReduceMax(class axis)
        ↓
[time] class indices + [time] max scores
        ↓
ReadbackAndClone
  2 values per timestep
        ↓
CPU CTC duplicate/blank filtering + string assembly
```

`UnityPaddleOcrRecognizerRuntime` must not call `Texture2D.ReadPixels`, `GetPixels32`, or use `RenderTexture.active` for input preprocessing. `Graphics.Blit` is deliberately retained for the GPU perspective crop and recognizer preprocessing shader; that is GPU-to-GPU work and is not an image readback.

The production runtime retains only the GPU-reduced recognizer worker. It does **not** retain a second full-output worker. `Execute()` exists only as a correctness oracle: when explicitly called by a parity/model probe it creates a temporary full-output worker, downloads the `[1,time,class]` matrix, and disposes that worker before returning. Live OCR calls `ExecuteReduced()` instead.

The recognizer preprocessing contract remains the Core `PaddleOcrV6TinyRecognitionPreprocess` contract:

- tensor layout: NCHW;
- channel order: BGR;
- row origin: top-left;
- model height: 48;
- default model width: 320;
- image resize preserves aspect ratio and uses `ceil(height * sourceWidth / sourceHeight)`, capped to model width;
- normalized image value: `(byte / 255 - 0.5) / 0.5`;
- unused right columns are **normalized zero**, matching PaddleOCR's zero-initialized tensor.

The live CTC reduction contract is:

- model output remains `[1,time,class]`;
- `class == dictionary token count + 1` for the CTC blank;
- GPU ArgMax uses `selectLastIndex=false`, matching NumPy/Paddle's first-index-on-ties behavior;
- CPU receives exactly one class index and one maximum score per timestep;
- class indices are range-checked and scores must be finite before Core CTC decoding;
- the full probability matrix is not read back in the live path.

`UnityPaddleOcrRecognizerRuntime.CreateReviewedPreprocessMaterial`, `CreateReviewedTextureTransform`, and `PopulateReviewedInputTensor` are production helpers reused by the real-Unity preprocessing parity probe. The GPU CTC reduction is separately compared against the retained full-output oracle.

## Real-Unity GPU preprocessing parity gates

Detector parity uses `PhraseLayerPaddleOcrGpuPreprocessProbe`. It creates a 736×736 oriented RGB fixture and runs it through the same production detector texture transform and normalization helpers. Because the fixture already matches the PP-OCR detector limit side length, the comparison does not depend on resize interpolation.

The detector probe verifies sampled values for:

1. top-left versus bottom-left row orientation;
2. BGR channel order;
3. raw texture-to-tensor values;
4. the GPU FunctionalGraph mean/std result against `PaddleOcrV6TinyDetectionPreprocess.NormalizeChannel`.

Recognizer preprocessing parity uses `PhraseLayerPaddleOcrRecognizerGpuPreprocessProbe`. Its fixture is 64×48 into a 96×48 model tensor. Height already matches the recognizer model, so `ResizedWidth` is exactly 64: the left 64 columns are one-to-one pixel-center samples and the right 32 columns are padding. This intentionally removes resize interpolation from the numerical comparison.

The recognizer preprocessing probe verifies:

1. top-left row orientation;
2. BGR channel order;
3. `(x-0.5)/0.5` normalization against `PaddleOcrV6TinyRecognitionPreprocess.NormalizeChannel`;
4. exact normalized-zero values in sampled right-padding columns;
5. the same production shader/material and `PopulateReviewedInputTensor` helper used by runtime inference.

## Real-Unity recognizer CTC reduction parity

`PhraseLayerPaddleOcrRecognizerGpuReductionProbe` is the correctness gate for the reduced output path. It runs the pinned recognizer twice on the same deterministic input:

1. the parity-only full-output path downloads `[1,time,class]` and serves as the oracle;
2. the production reduced path keeps that tensor GPU-side and downloads only ArgMax indices plus ReduceMax scores.

The gate requires:

- identical `[1,time,class]` shape metadata;
- the full oracle to contain finite values in `[0,1]`;
- exact winning class index equality at every timestep;
- maximum-score error at or below `1e-6` at every timestep;
- exact decoded text;
- exact emitted-token count;
- CTC confidence parity.

The CPU oracle uses a strict `>` update, so ties retain the first class index. Unity Inference Engine 2.2.1's functional ArgMax is bound to `selectLastIndex=false`, matching that rule.

`tools/unity/verify-local-ocr-inference.sh` chains this reduction parity gate after the pinned detector/recognizer synthetic inference probe. As a result, self-hosted Read Mode workflows that use the shared OCR host gate cannot package or install an APK unless full-vs-reduced parity has first passed in real Unity. This is still a host graphics gate, not Quest evidence.

These gates prove the reviewed preprocessing/reduction math and Unity tensor semantics on that runner. They do **not** by themselves prove that a Meta passthrough texture and its cached camera pose refer to the exact same physical exposure on Quest 3.

## Camera timestamp / pose claim boundary

The Meta camera bridge retains a stable `PassthroughCameraAccess.Timestamp` + `GetCameraPose()` pair with each accepted `ImageFrame`, and spatial projection reuses that captured pose for center and corner rays.

Removing detector and recognizer input CPU image readback closes known avoidable stalls between camera capture, detector submission, crop preparation, and recognition submission. Reducing recognizer output also avoids downloading the full class matrix. However, the Meta texture producer, GPU command execution, detector output readback, and reduced recognizer readback remain asynchronous/synchronizing boundaries. Therefore device evidence must continue to report:

```text
camera_timestamp_pose_binding_implemented=true
camera_pixel_pose_sync_verified=false
```

until a real Quest 3 timing/visual gate demonstrates the stronger pixel↔pose synchronization claim. Do not infer that claim from Hosted CI or from any real-Unity preprocessing/reduction parity probe.

## GPU-residency claim boundary

The OCR image path no longer performs CPU image readback between camera texture, detector input, perspective crop, and recognizer input. The live recognizer also no longer downloads its full probability matrix. This still does **not** mean the entire OCR algorithm is GPU-resident:

- detector probability maps are copied to CPU for DB post-processing and quad generation;
- recognizer class indices and maximum scores are copied to CPU for CTC duplicate/blank filtering and string assembly;
- OCR observation assembly and semantic alignment remain CPU/Core work.

Accordingly, documentation and evidence may say **detector/recognizer image preprocessing and recognizer CTC reduction are GPU-side**. They must not claim end-to-end GPU-resident OCR. Physical performance, synchronization cost, thermals, and sustainable OCR frequency remain Quest 3 measurement questions.

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

The only persisted text diagnostic is `quest-read-mode-diagnostics.txt`. Each candidate line must fully match one of the reviewed counter/status grammars in `SAFE_DIAGNOSTIC_PATTERNS`; matching a safe prefix is insufficient. The recognizer runtime line is limited to `recognizer_gpu_ctc_reduction=<state> full_output_worker_retained=<state>`. Appending recognized text or another suffix causes the whole line to be discarded.

The persisted diagnostics are limited to smoke state, timings, counts, recognizer runtime booleans, captured-pose counters, MRUK status/confidence, layout/mask/render counters, compact OCR stage, and the literal `FATAL EXCEPTION` marker. Fatal stack/message content is discarded. `recognized_text=` and `display_text=` are not valid diagnostic grammars.

ADB serials are excluded from evidence. The JSON stores only a truncated SHA-256 fingerprint. Failed external commands serialize only their exit code: raw command arguments and raw stderr are deliberately excluded because they can contain device identifiers, platform details, or future app/runtime text. The Quest workflow uploads explicit safe evidence files rather than a wildcard device-output directory.

This privacy boundary is part of the device gate contract, not a logging convention. Reintroducing raw logcat persistence, raw stderr/command serialization, wildcard Quest output upload, or recognized/display text in the diagnostic grammar must fail Hosted validation before a device gate can be considered valid.

## Production status

PP-OCRv6 Tiny detection + recognition is no longer only an abstract candidate. The Unity adapter has a pinned Inference Engine 2.2.1 detector/recognizer implementation, local asset staging, model/dictionary contract probes, guarded Hosted compile coverage, detector/recognizer preprocessing parity probes, and a full-vs-reduced recognizer CTC parity gate.

The live runtime now keeps detector/recognizer image preparation on the GPU and retains only the reduced recognizer worker. It downloads the detector DB map plus two recognizer values per timestep. Actual real-Unity parity execution and real Quest 3 execution remain self-hosted gates and must not be inferred from Hosted compile/static validation.

Remaining production gates include real Unity execution with the pinned local assets, real Quest 3 execution, pixel↔pose timing evidence, visual quality/stereo comfort, and measured performance/thermal behavior. Model revisions/files/licenses remain pinned and reviewed separately; Core must not acquire Unity-specific tensor or graphics dependencies.
