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

`UnitySpatialProjectionBehaviour` connects `MetaPassthroughCameraBridge` + `UnityPhysicsSurfaceRaycaster` to the platform-neutral projection and world-text layout planners.

## Four-corner physical text-plane fitting

A successful center hit is not enough to cover physical source text. For an `InPlaceReplacement` target, `WorldTextLayoutPlanner` independently projects all four corners of the semantic OCR envelope:

```text
ProjectedAssistanceTarget
        ↓ OCR envelope corners
4 × ViewportPointToRay
        ↓
4 × surface raycast
        ↓
normal-consistency gate
        ↓
planarity gate
        ↓
WorldTextSurface
  - center
  - viewport-preserving right/up axes
  - layout normal
  - width/height in meters
  - maximum plane residual
```

Default acceptance limits are deliberately conservative:

- maximum corner-to-plane residual: `0.03 m`;
- minimum fitted width/height: `0.005 m`;
- minimum corner-normal alignment with the averaged surface normal: dot product `0.80`.

These are implementation defaults, **not Quest-validated perceptual thresholds**. They must be tuned from real-device fixtures rather than treated as product-quality constants.

Every corner must have both a valid viewport ray and a surface hit. Missing corners, divergent normals, degenerate extents, or excessive non-planarity prevent in-place layout. PhraseLayer does not extrapolate a missing corner from the center hit.

Collider normal sign is not used to flip recognized text. `WorldTextSurface.Right` and `.Up` preserve viewport orientation, while `.Normal` is canonicalized to the corresponding right-handed layout frame. This prevents a collider's front/back convention from silently mirroring or inverting the OCR text orientation.

`UnityWorldTextLayoutDebugBehaviour` can draw the fitted metric envelope in world space for Quest registration checks. It is a verification visualization, not the final text replacement renderer.

## Conservative overlay policy

Physical text covering is confidence-sensitive:

| OCR/semantic coverage | Successful center projection | Four-corner fit | Default placement |
|---|---|---|---|
| Exact | yes | valid | eligible for `InPlaceReplacement` |
| Exact | yes | invalid | no in-place world text |
| Partial | yes | n/a | `AdjacentLabel` only |
| Unresolved | n/a | n/a | `Skip` |
| Any resolved coverage | ray unavailable | n/a | `Skip` |
| Any resolved coverage | surface not found | n/a | `Skip` |

This is deliberately conservative. A translation should not be painted over the wrong physical phrase merely to maximize visible assistance.

Later UX experiments may introduce a screen-space fallback, but it should be a distinct rendering mode rather than silently pretending world registration succeeded.

## What remains platform-specific / unverified

- Quest runtime permission behavior and camera timestamps;
- actual Quest environment collider/depth source used by `UnityPhysicsSurfaceRaycaster`;
- temporal smoothing/tracking across successive OCR observations;
- the production world-space Japanese text renderer, font sizing, occlusion/masking, and source-text covering;
- real Quest device registration error and performance/thermal measurements.

A Unity Physics adapter or a successful four-corner fit in host tests is **not** evidence that Quest environment surfaces are available or correctly registered. That claim requires a real-device test with the chosen environment-mesh/depth source.
