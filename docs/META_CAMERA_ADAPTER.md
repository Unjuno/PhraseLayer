# Meta Passthrough Camera adapter

PhraseLayer keeps Meta Quest camera APIs behind a thin Unity bridge while preserving a platform-neutral Core.

## Current upstream baseline

The Unity project is pinned to the same baseline currently used by Meta's `oculus-samples/Unity-PassthroughCameraApiSamples` reference project:

```text
Unity                  6000.0.66f2
com.meta.xr.mrutilitykit 85.0.0
com.unity.ai.inference 2.2.1
com.unity.xr.management 4.5.4
com.unity.xr.openxr    1.15.1
com.unity.ugui         2.0.0
```

`tools/validate_repo.py` treats these as deliberate pins. Upgrade them only together with an explicit baseline review and rerun the Unity/Quest validation gates.

## Native image payload

`ImageFrame` now supports either:

- CPU `byte[]` pixels; or
- an `IImageFramePayload` owned by a platform adapter.

Unity uses `UnityTextureFramePayload` to carry the camera `Texture` without an automatic GPU→CPU copy. A future OCR adapter can therefore feed Unity AI Inference or another GPU-capable runtime directly. CPU readback remains an explicit fallback, not a camera-layer requirement.

## Permission adapter

`MetaPassthroughCameraPermissionService` uses the two permissions present in Meta's current camera utilities:

```text
android.permission.CAMERA
horizonos.permission.HEADSET_CAMERA
```

On Android it uses Unity's `Permission.RequestUserPermissions` + `PermissionCallbacks`, following the callback-based pattern used by Meta's Spatial Lingo camera utility. On non-Android Editor builds it reports granted so synthetic/editor workflows remain usable.

Only one request is allowed per service instance at a time. The application should own one camera permission service rather than creating competing permission requesters.

## Camera bridge

`MetaPassthroughCameraBridge` implements:

```text
ICameraStreamBackend
IViewportRayProvider
```

It accepts the actual Meta `PassthroughCameraAccess` component as a serialized `UnityEngine.Component`. At runtime it verifies and caches the public API contract:

```text
bool IsPlaying
Texture GetTexture()
Ray ViewportPointToRay(Vector2)
```

This keeps Meta types out of `PhraseLayer.Core` and localizes SDK API drift to one Unity bridge. If Meta changes one of these signatures, the bridge fails with a descriptive error rather than silently reconstructing camera rays.

## Camera frame timestamp

The current bridge records a local monotonic observation timestamp using Unity `Time.realtimeSinceStartupAsDouble`. This is deliberately **not** claimed to be the Passthrough camera hardware timestamp.

Before world-registration benchmarks are considered valid, the exact current PCA timestamp contract must be verified in a real Unity/Quest build and, where available, used to align image observations with camera pose/depth data.

## Remaining Gate 4 work

- resolve the pinned Meta packages in a real Unity Editor;
- assign/create the real `PassthroughCameraAccess` component in the scene;
- verify Android runtime permission callbacks on Quest 3;
- confirm `IsPlaying`, `GetTexture` and `ViewportPointToRay` signatures against the installed package;
- select and integrate the first OCR engine against `UnityTextureFramePayload`;
- verify real camera timestamps / pose synchronization;
- benchmark capture + OCR latency, PSS, XR frame time and thermal behavior on device.

No real Quest performance or camera functionality is claimed until those checks pass.
