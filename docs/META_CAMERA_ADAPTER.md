# Meta Passthrough Camera adapter

PhraseLayer keeps Meta Quest camera APIs behind a thin Unity bridge while preserving a platform-neutral Core.

## Current upstream baseline

The Unity project is pinned to the same baseline used when the current adapter/runtime contract was established:

```text
Unity                    6000.0.66f2
com.meta.xr.mrutilitykit 85.0.0
com.unity.ai.inference   2.2.1
com.unity.xr.management  4.5.4
com.unity.xr.openxr      1.15.1
com.unity.ugui           2.0.0
```

`tools/validate_repo.py` treats these as deliberate pins. Upgrade them only together with an explicit upstream review and rerun the Unity/Quest validation gates.

## Native image payload

`ImageFrame` supports either:

- CPU `byte[]` pixels; or
- an `IImageFramePayload` owned by a platform adapter.

Unity uses `UnityTextureFramePayload` to carry the camera `Texture` without an automatic GPU→CPU copy. The PP-OCR Unity Inference Engine path therefore consumes the camera texture without forcing a camera-layer readback. CPU readback remains an explicit fallback, not a camera-layer requirement.

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

It accepts the actual Meta `PassthroughCameraAccess` component as a serialized `UnityEngine.Component`. At runtime it verifies and caches this public API contract:

```text
bool IsPlaying
Texture GetTexture()
Ray ViewportPointToRay(Vector2)
```

This keeps Meta types out of `PhraseLayer.Core` and localizes SDK API drift to one Unity bridge. If Meta changes one of these signatures, the bridge fails with a descriptive error rather than silently reconstructing camera rays.

`PhraseLayerEditorSetup.CreateDemoScene` now resolves `Meta.XR.PassthroughCameraAccess` from loaded Unity assemblies without a compile-time Meta reference, adds it to the demo scene, validates the bridge contract, and wires:

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
```

Local pinned model/dictionary assets remain git-ignored and are assigned through `PhraseLayerLocalOcrAssets`; scene wiring does not silently bundle model binaries.

## Unity thread-affinity contract

Camera, graphics, and Unity Inference Engine operations may be main-thread/owner-thread-bound. Core camera/OCR/language orchestration therefore preserves the caller `SynchronizationContext` across platform-adapter awaits. Do not reintroduce `ConfigureAwait(false)` across those adapter boundaries unless the concrete runtime is explicitly redesigned to marshal back to its required thread.

Host regression tests run delayed thread-affine fake adapters under a dedicated synchronization context to catch this class of failure before Quest deployment.

## Camera frame timestamp

The current bridge records a local monotonic observation timestamp using Unity `Time.realtimeSinceStartupAsDouble`. This is deliberately **not** claimed to be the Passthrough camera hardware timestamp.

Before world-registration benchmarks are considered valid, the exact current PCA timestamp contract must be verified in a real Unity/Quest build and, where available, used to align image observations with camera pose/depth data.

## Remaining Gate 4 work

- resolve and verify the pinned Meta packages in the real Unity environment used for the Quest build;
- prepare/assign the pinned local PP-OCR assets and run the real Unity inference probe;
- create the wired demo scene and confirm the reflected `PassthroughCameraAccess` API contract against the installed package;
- verify Android runtime permission callbacks on Quest 3;
- run camera → PP-OCR → viewport presentation on Quest 3;
- verify real camera timestamps / pose synchronization;
- benchmark capture + OCR latency, PSS, XR frame time and thermal behavior on device.

No real Quest performance or camera functionality is claimed until those checks pass.
