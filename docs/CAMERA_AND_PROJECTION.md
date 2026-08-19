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

This mirrors the lifecycle observed in Meta's current CameraViewer reference: permission must be available, the Passthrough camera stream must reach its playing state, and only then should frames be consumed.

A future Meta implementation should map:

- `ICameraPermissionService` → the platform Passthrough-camera permission mechanism;
- `ICameraStreamBackend.IsPlaying` → `PassthroughCameraAccess.IsPlaying`;
- stream capture → `PassthroughCameraAccess.GetTexture()` followed by the selected CPU/GPU OCR input path.

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

The Meta adapter should implement `IViewportRayProvider` by delegating to the real Passthrough Camera API `ViewportPointToRay` call. PhraseLayer must not reconstruct a ray from an assumed symmetric field of view or assume viewport center is the optical axis.

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

## What remains platform-specific

- Quest camera permission API;
- Passthrough stream startup and texture access;
- texture→OCR tensor/pixel transfer;
- `ViewportPointToRay` conversion;
- depth/environment raycast;
- world-space orientation, text-plane fitting, smoothing, tracking and anchors;
- device performance/thermal measurements.
