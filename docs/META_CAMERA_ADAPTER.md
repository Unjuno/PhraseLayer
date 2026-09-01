# Meta Passthrough Camera adapter

PhraseLayer keeps Meta Quest camera APIs behind a thin Unity bridge while preserving a platform-neutral Core.

## Current upstream baseline

The Unity project is pinned to the baseline used for the current adapter/runtime contract:

```text
Unity                    6000.0.66f2
com.meta.xr.mrutilitykit 85.0.0
com.unity.ai.inference   2.2.1
com.unity.xr.management  4.5.4
com.unity.xr.openxr      1.15.1
com.unity.xr.meta-openxr 2.2.1
com.unity.ugui           2.0.0
```

`tools/validate_repo.py` treats these as deliberate pins. Upgrade them only together with an explicit upstream review and rerun the Unity/Quest validation gates.

## Native image payload and capture metadata

`ImageFrame` supports either:

- CPU `byte[]` pixels; or
- an `IImageFramePayload` owned by a platform adapter.

Unity uses `UnityTextureFramePayload` to carry the Meta-managed camera `Texture`. A real Passthrough Camera capture now also carries:

```text
PassthroughCameraAccess.Timestamp
PassthroughCameraAccess.GetCameraPose()
```

The timestamp is converted from .NET 100 ns ticks to an opaque microsecond ordering/identity value (`Ticks / 10`). It is **not** described as Unix time.

The bridge reads Timestamp → camera pose → texture → Timestamp and accepts the pose metadata only when the two timestamp reads are identical. A bounded three-attempt retry covers a camera-frame boundary racing capture. If all attempts straddle a boundary, OCR may still use the texture, but that frame is marked as lacking trusted capture-pose metadata and cannot satisfy the Quest Read Mode captured-pose gate.

## Permission adapter

`MetaPassthroughCameraPermissionService` uses the two permissions present in the current PhraseLayer camera contract:

```text
android.permission.CAMERA
horizonos.permission.HEADSET_CAMERA
```

On Android it uses Unity's `Permission.RequestUserPermissions` + `PermissionCallbacks`. On non-Android Editor builds it reports granted so synthetic/editor workflows remain usable.

Only one request is allowed per service instance at a time. The application should own one camera permission service rather than creating competing permission requesters.

## Camera bridge

`MetaPassthroughCameraBridge` implements:

```text
ICameraStreamBackend
IViewportRayProvider
```

It accepts the actual Meta `PassthroughCameraAccess` component as a serialized `UnityEngine.Component`. At runtime it verifies and caches the reviewed v85 public API contract:

```text
bool IsPlaying
Texture GetTexture()
DateTime Timestamp
Pose GetCameraPose()
Ray ViewportPointToRay(Vector2, Pose?)
```

The optional-pose overload matters. `TryCreateFrameRayProvider(ImageFrame, ...)` creates a frame-bound `IViewportRayProvider` when the frame contains trusted capture metadata. Both the OCR envelope center and all four physical-layout corners then call `ViewportPointToRay` with the same cached camera pose even if semantic/language processing completed later.

Frames without trusted capture metadata retain the ordinary current-pose provider for Editor/synthetic compatibility, but the Quest Read Mode smoke test explicitly rejects that fallback.

This keeps Meta types out of `PhraseLayer.Core` and localizes SDK API drift to one Unity bridge. If Meta changes one of these signatures, the bridge fails descriptively rather than silently reconstructing camera rays.

`PhraseLayerEditorSetup.CreateDemoScene` resolves `Meta.XR.PassthroughCameraAccess` from loaded Unity assemblies without a compile-time Meta reference, adds it to the demo scene, validates the bridge contract, and wires:

```text
Meta PassthroughCameraAccess
        ↓
MetaPassthroughCameraBridge
        ↓
OcrDebugRuntimeBehaviour
        ↓
UnityPaddleOcrBootstrapBehaviour / UnityPaddleOcrEngine
        ↓
OcrViewportDebugBehaviour
        ↓
frame-bound Read Mode projection
```

Local pinned model/dictionary assets remain git-ignored and are assigned through `PhraseLayerLocalOcrAssets`; scene wiring does not silently bundle model binaries.

## Unity thread-affinity contract

Camera, graphics, and Unity Inference Engine operations may be main-thread/owner-thread-bound. Core camera/OCR/language orchestration therefore preserves the caller `SynchronizationContext` across platform-adapter awaits. Do not reintroduce `ConfigureAwait(false)` across those adapter boundaries unless the concrete runtime is explicitly redesigned to marshal back to its required thread.

Host regression tests run delayed thread-affine fake adapters under a dedicated synchronization context to catch this class of failure before Quest deployment.

## What timestamp/pose synchronization now means

Implemented:

- the source timestamp is `PassthroughCameraAccess.Timestamp`, not Unity observation time;
- the camera pose is cached with `GetCameraPose()` during capture;
- the exact `ImageFrame` retains that timestamp/pose pair;
- delayed Read Mode projection reuses the cached pose through `ViewportPointToRay(Vector2, Pose?)`;
- Quest smoke requires captured-pose rays and rejects current-pose-only projection.

Still **not verified**:

- exact pixel ↔ timestamp/pose identity through the whole PP-OCR preprocessing path.

`UnityPaddleOcrDetectorRuntime` currently performs a blocking `Graphics.Blit` followed by `ReadPixels` while resizing/normalizing the camera texture. Meta documents render-thread timing caveats for blocking copies of Passthrough Camera textures. Therefore PhraseLayer records `camera_pixel_pose_sync_verified=false` even though timestamp/pose binding is implemented. Closing that claim requires a reviewed frame-consumption path plus real Quest timing/registration evidence.

## Remaining Gate 4 work

- resolve and verify the pinned Meta packages in the real Unity environment used for the Quest build;
- prepare/assign the pinned local PP-OCR assets and run the real Unity inference probe;
- create the wired demo scene and confirm the reflected v85 `PassthroughCameraAccess` API contract against the installed package;
- verify Android runtime permission callbacks on Quest 3;
- run camera → PP-OCR → captured-pose projection on Quest 3;
- replace or validate the current blocking PP-OCR camera-texture preprocessing so pixel/pose synchronization can be claimed;
- benchmark capture + OCR latency, PSS, XR frame time and thermal behavior on device.

No real Quest performance, registration accuracy, or complete pixel/pose synchronization is claimed until those checks pass.
