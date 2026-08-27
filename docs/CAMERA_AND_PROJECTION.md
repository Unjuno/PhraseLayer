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

The Unity Quest adapter maps this contract to Meta Passthrough Camera Access: camera permission must be available, the passthrough camera stream must reach its playing state, and only then are frames consumed.

- `ICameraPermissionService` → the local Quest passthrough-camera permission bridge;
- `ICameraStreamBackend.IsPlaying` → reflected `PassthroughCameraAccess.IsPlaying`;
- stream capture → `PassthroughCameraAccess.GetTexture()` followed by the local OCR input path.

Core does not import `Meta.XR`, `OVRPermissionsRequester`, `Texture2D`, or Android types.

## Viewport to world

After semantic assistance has been aligned with OCR geometry:

```text
SpatialAssistanceTarget
        ↓ stabilized envelope center
IViewportRayProvider
        ↓
SpatialRay
        ↓
ISurfaceRaycaster
        ↓
SurfaceHit
        ↓ verified-hit stabilizer
ProjectedAssistanceTarget / world label
```

`MetaPassthroughCameraBridge` implements `IViewportRayProvider` by delegating to the real Passthrough Camera API `ViewportPointToRay` call. PhraseLayer does not reconstruct a ray from an assumed symmetric field of view and does not assume viewport center is the optical axis.

For Quest surface resolution, `QuestSurfaceRaycaster` uses this order:

1. MRUK's native OpenXR environment raycaster when its native delegates are initialized and `com.oculus.permission.USE_SCENE` has been granted;
2. ordinary Unity collider geometry through `Physics.Raycast`;
3. no world hit, allowing the presentation layer to retain the viewport fallback.

The first path deliberately bypasses Meta's `EnvironmentRaycastManager` component. The reviewed MRUK implementation sends a telemetry event from that component's `Start()` method, which conflicts with PhraseLayer's reference-runtime boundary. PhraseLayer instead reflects the already-loaded internal `MRUKNativeFuncs` delegates (`CreateEnvironmentRaycaster`, `EnvironmentRaycasterStatus`, `RaycastEnvironment`, `DestroyEnvironmentRaycaster`) and never creates the manager component.

PhraseLayer also does not instantiate `EnvironmentDepthManager` for this path. The reviewed Core SDK implementation expects an `OVRCameraRig` during its update loop, while the current PhraseLayer MVP intentionally uses Unity XR head pose with the OpenXR tracking origin at Unity world origin. Introducing an OVR rig solely for depth would create a second tracking-space authority.

The native adapter therefore has a strict coordinate contract: the committed Read MVP's Unity world coordinates equal its OpenXR tracking-space coordinates. If a moved or scaled tracking origin is introduced later, the native adapter must be updated at the same time. It must not guess a transform.

Permission denial, unavailable native delegates, creating/not-ready native state, unsupported calls, or invocation failure all fail closed into the next fallback rather than fabricating distance. If PhraseLayer created the global native environment raycaster itself, it tracks that ownership and releases it on overlay shutdown; a raycaster that was already ready is treated as externally owned and is not destroyed by PhraseLayer.

## Verified surface-hit stabilization

Viewport-box stabilization alone is insufficient for a comfortable MR label: environment-depth or collider hits can move slightly even when OCR geometry is stable. `SurfaceHitStabilizer` therefore runs only after an `ISurfaceRaycaster` has produced a real hit.

Its default policy is intentionally bounded:

- small point and normal changes blend with a `0.35` newest-hit factor;
- point movement over `0.20 m` is accepted immediately rather than lagged;
- normal changes over `20°` are accepted immediately rather than blended across a different plane;
- one consecutive raycast miss may reuse the last verified hit for an already-rendered semantic unit;
- a second consecutive miss drops the state and returns that unit to the viewport fallback;
- every Read encounter change resets all world-hit state.

This state is keyed by semantic unit ID. It cannot create a new world placement, cannot promote Partial/Unresolved OCR coverage to Exact, and cannot carry a hit from one encounter into another. The retained miss is therefore temporal continuity for previously verified geometry, not guessed depth.

## Conservative overlay policy

Physical text covering is confidence-sensitive:

| OCR/semantic coverage | Successful surface projection | Default placement |
|---|---|---|
| Exact | yes | `InPlaceReplacement` / world label |
| Partial | yes | Core permits `AdjacentLabel`; current Quest world renderer keeps partial targets on its viewport/debug path |
| Unresolved | n/a | no new world placement |
| Any resolved coverage | ray unavailable | viewport fallback |
| Any resolved coverage | surface not found | viewport fallback, except the single bounded retention of a previously verified hit |

The current Quest renderer suppresses its 2D GUI target only after the same semantic unit has a verified world-surface placement. A previously verified world label may retain the same world pose for the Core viewport stabilizer's bounded two-observation OCR-dropout window. Separately, a single transient surface-raycast miss may retain the last verified `SurfaceHit`. Neither path reprojects from guessed depth.

This is deliberately conservative. A translation should not be painted over the wrong physical phrase merely to maximize visible assistance.

## What remains platform-specific or device-gated

- Quest 3 confirmation of passthrough permission/startup and real frame capture;
- locally staged PP-OCR execution on camera frames;
- native environment-raycaster startup/permission behavior and hit accuracy on Quest 3;
- tuning the world-hit smoothing thresholds from measured Quest 3 motion/latency rather than desktop assumptions;
- world-label orientation/readability and physical text-plane fitting on device;
- longer-term world persistence/anchor strategy if needed;
- local OPUS-MT execution and quality/latency validation;
- device performance, memory, battery and thermal measurements.
