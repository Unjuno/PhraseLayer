# Camera lifecycle and spatial projection policy

PhraseLayer keeps the Quest/Meta implementation thin by defining the camera lifecycle and spatial projection contracts in platform-neutral Core.

## Camera lifecycle

`CameraCaptureCoordinator` orchestrates two adapters:

```text
ICameraPermissionService
        +
ICameraStreamBackend
        ↓
CameraCaptureCoordinator
```

States:

```text
Stopped
  ↓
WaitingForPermission
  ↓
Starting
  ↓
Ready
```

Any denied permission, start failure, missing frame, or backend exception moves the coordinator to `Failed` with a diagnostic `FailureReason`. Cancellation returns it to `Stopped` rather than treating cancellation as a device failure.

The Unity/Meta adapter maps the platform boundary as follows:

- `ICameraPermissionService` → the platform Passthrough-camera permission mechanism;
- `ICameraStreamBackend.IsPlaying` → reflected `PassthroughCameraAccess.IsPlaying`;
- stream capture → reflected `PassthroughCameraAccess.GetTexture()` carried as `UnityTextureFramePayload`.

Core does not import `Meta.XR`, `OVRPermissionsRequester`, `Texture2D`, or Android types.

## Viewport to world

After semantic assistance has been aligned with OCR geometry:

```text
SpatialAssistanceTarget
        ↓ envelope center
IViewportRayProvider
        ↓
SpatialRay
        ↓
ISurfaceRaycaster
        ↓
SurfaceHit
        ↓
ProjectedAssistanceTarget
```

`MetaPassthroughCameraBridge` implements `IViewportRayProvider` by delegating to the real Passthrough Camera API `ViewportPointToRay` call. PhraseLayer does not reconstruct a ray from an assumed symmetric field of view and does not assume viewport center is the optical axis.

`UnityPhysicsSurfaceRaycaster` implements `ISurfaceRaycaster` over `UnityEngine.Physics.Raycast`. It normalizes the Core ray direction, validates finite coordinates and a positive maximum range, and converts a successful Unity `RaycastHit` into the platform-neutral `SurfaceHit` contract.

The adapter deliberately does **not** assume where physical-environment colliders come from. A scene may provide controlled test colliders or a reviewed Quest/MR environment-mesh path. If no valid collider is hit, projection fails as `SurfaceNotFound` rather than inventing depth.

`UnitySpatialProjectionBehaviour` connects `MetaPassthroughCameraBridge` + `UnityPhysicsSurfaceRaycaster` to the existing `SpatialProjectionPlanner`. The generated demo scene now includes this bridge, but rendering/tracking is still separate.

## Conservative overlay policy

Physical text covering is confidence-sensitive:

| OCR/semantic coverage | Successful surface projection | Default placement |
|---|---|---|
| Exact | yes | `InPlaceReplacement` |
| Partial | yes | `AdjacentLabel` |
| Unresolved | n/a | `Skip` |
| Any resolved coverage | ray unavailable | `Skip` |
| Any resolved coverage | surface not found | `Skip` |

This is deliberately conservative. A translation should not be painted over the wrong physical phrase merely to maximize visible assistance.

Later UX experiments may introduce a screen-space fallback, but it should be a distinct rendering mode rather than silently pretending world registration succeeded.

## What remains platform-specific / unverified

- Quest runtime permission behavior and camera timestamps;
- actual Quest environment collider/depth source used by `UnityPhysicsSurfaceRaycaster`;
- world-space orientation and text-plane fitting across the OCR quad rather than only an envelope-center hit;
- smoothing, tracking, anchors, occlusion/masking, and source-text covering;
- real Quest device performance/thermal measurements.

A Unity Physics adapter existing in the repository is **not** evidence that Quest environment surfaces are available or correctly registered. That claim requires a real-device test with the chosen environment-mesh/depth source.
