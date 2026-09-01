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

- `ICameraPermissionService` → `android.permission.CAMERA` + `horizonos.permission.HEADSET_CAMERA` on Android;
- `ICameraStreamBackend.IsPlaying` → reflected `PassthroughCameraAccess.IsPlaying`;
- stream capture → reflected `PassthroughCameraAccess.GetTexture()` carried as `UnityTextureFramePayload`;
- viewport rays → reflected `PassthroughCameraAccess.ViewportPointToRay(Vector2)`.

Core does not import `Meta.XR`, Android permission types, Unity textures, or MRUK runtime types.

### Camera timestamp status

`MetaPassthroughCameraBridge` currently timestamps each observed texture with `Time.realtimeSinceStartupAsDouble` when PhraseLayer receives the frame. This is **local observation time**, not a verified Passthrough Camera hardware timestamp.

The Quest Read Mode evidence therefore records:

```text
camera_timestamp_source=unity-realtime-observation
camera_hardware_timestamp_pose_sync_verified=false
```

Hardware timestamp / camera-pose / depth synchronization remains a separate device gate. The current timestamp must not be interpreted as sensor-time registration evidence.

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

`MetaPassthroughCameraBridge` implements `IViewportRayProvider` by delegating to the real Passthrough Camera `ViewportPointToRay` call. PhraseLayer does not reconstruct a ray from an assumed symmetric field of view and does not assume viewport center is the optical axis.

### Quest path: MRUK live environment depth

The generated Quest demo/fixture scene uses:

```text
Meta.XR.EnvironmentRaycastManager
        ↓
UnityEnvironmentSurfaceRaycaster
        ↓
ISurfaceRaycaster
```

`UnityEnvironmentSurfaceRaycaster` validates the pinned MRUK API boundary at runtime before it can report a hit:

- exact manager type `Meta.XR.EnvironmentRaycastManager`;
- static `bool IsSupported`;
- `bool Raycast(Ray, out EnvironmentRaycastHit, float)`;
- hit point and normal;
- optional normal-confidence and status diagnostics.

A false/unsupported/not-ready environment raycast remains `SurfaceNotFound`; PhraseLayer never fabricates depth. The Quest smoke gate additionally requires `projection.UsesEnvironmentRaycast` and a successfully validated MRUK ABI, so a Unity Physics fallback cannot accidentally satisfy the hardware gate.

This path does **not** require a prior room Scene scan or generated Physics environment colliders. It is intended to use the Quest live environment-depth raycast supplied by the pinned Meta stack.

### Controlled geometry path

`UnityPhysicsSurfaceRaycaster` remains available for editor tests, controlled fixtures, and explicit caller-provided geometry. It normalizes the Core ray direction and converts a successful `Physics.Raycast` into `SurfaceHit`.

It is not the default Quest fixture path and cannot satisfy `QuestReadModeSmokeTestBehaviour` by itself.

`UnitySpatialProjectionBehaviour` can consume either adapter through the same Core `ISurfaceRaycaster` boundary, but chooses the MRUK environment adapter when it is configured.

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

Surface-normal sign is not used to flip recognized text. `WorldTextSurface.Right` and `.Up` preserve viewport orientation, while `.Normal` is canonicalized to the corresponding right-handed layout frame. This prevents a surface provider's front/back convention from silently mirroring or inverting the OCR text orientation.

`UnityWorldTextLayoutDebugBehaviour` can draw the fitted metric envelope in world space for registration checks. It is a verification visualization, not the final text replacement renderer.

## Clean-checkout Quest project setup

The repository intentionally does not commit machine-generated Unity XR settings as authoritative evidence. Before the Read Mode fixture build, `PhraseLayerQuestProjectSetup` invokes the pinned Meta Project Setup Tool's public `OVRProjectSetup.FixAllAsync(BuildTargetGroup.Android)` path in a dedicated Unity process, saves the resulting Required Quest settings, then starts a fresh Unity process for the Android build.

The reviewed package pins currently include:

- MRUK `85.0.0`;
- Unity OpenXR `1.15.1`;
- Unity OpenXR: Meta `2.2.1`.

Host CI verifies this setup/build ordering structurally. Only a real self-hosted Unity run proves that the pinned Meta packages still apply valid Android Quest settings.

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

## What is implemented but not yet Quest-proven

The branch now contains a reproducible fixture path for:

```text
Quest Passthrough Camera
        ↓
PP-OCR
        ↓
adaptive Read Mode planning
        ↓
semantic ↔ OCR geometry
        ↓
MRUK live-depth four-corner fit
        ↓
world tracking
        ↓
source mask + Japanese world text
```

`QuestReadModeSmokeTestBehaviour` requires the real OCR smoke, MRUK environment raycast, layout-ready world text, current observed tracks, source-mask rendering, and world-text rendering before emitting PASS.

Still unverified until a real Quest 3 workflow run produces evidence:

- actual camera permission/runtime behavior;
- actual MRUK environment-depth availability and registration error;
- hardware camera timestamp / pose / depth synchronization;
- stereo visual alignment and source-mask quality;
- Japanese font appearance on headset;
- frame-time, memory, thermal, and battery behavior.

Host tests and Unity shell compilation are not substitutes for those measurements.
