# PhraseLayer local-only runtime contract

## Product boundary

**Local is the reference runtime.** The official PhraseLayer build is designed to run without a PhraseLayer server, account, API key, telemetry backend, or runtime cloud dependency.

- **No PhraseLayer backend.** Learner state and encounter history remain on the device.
- Camera frames and microphone audio are processed locally by the reference implementation.
- OCR, ASR, semantic analysis, learner-state inference, assistance selection, translation, and MR surface placement are local runtime responsibilities.
- **No automatic cloud fallback.** If a local engine cannot produce a result, PhraseLayer may preserve the source or report an unresolved state; it must not silently upload data.
- The official Quest project must not request Android `INTERNET` or `ACCESS_NETWORK_STATE` permissions.
- Runtime analytics/telemetry SDKs are not part of the reference distribution.

## Device-local camera and spatial permissions

Local-only does not mean sensor-free. Read Mode needs explicit device permissions for data that remains on the headset:

- `android.permission.CAMERA` and `horizonos.permission.HEADSET_CAMERA` are used for passthrough camera frames;
- `com.oculus.permission.USE_SCENE` is used for Meta Environment Depth / Spatial Data raycasts so an OCR viewport point can be placed on a measured physical surface;
- `com.oculus.permission.USE_ANCHOR_API` is not part of the current Read baseline because PhraseLayer does not persist spatial anchors yet.

Spatial Data permission is requested only for world-surface placement. If the permission is denied, unavailable, or Environment Depth is unsupported/not ready, the Read pipeline falls back to collider-backed placement and then to the viewport overlay. No cloud fallback is introduced.

## Extensibility boundary

**Provider interfaces remain replaceable.** `IOcrEngine`, `IAsrEngine`, and `ITranslationEngine` stay platform-neutral so researchers and developers can swap local models and runtimes without changing the language-learning pipeline.

Keeping an interface does **not** mean the official app includes remote providers. Remote/API adapters are intentionally outside the initial reference distribution. A future community fork or separately reviewed extension can implement those interfaces, but it must make any changed data boundary explicit.

## Why this is enforced in code

Privacy is not only a settings-screen promise. The repository enforces the local-only reference contract in two places:

1. `tools/validate_local_only.py` rejects reviewed runtime networking APIs, direct Unity cloud-service dependencies, and network permissions in checked-in Android manifests while pinning the reviewed local camera/Spatial Data permissions.
2. `PhraseLayerLocalOnlyBuildGuard` fails Unity builds that force Android Internet permission or add reviewed runtime network APIs.

The build guard also exposes a menu action that disables forced Internet and external-storage permissions for the Quest project.

## Non-goals

This contract does not claim that every third-party Unity/Meta package contains no dormant networking code internally. The stronger release gate should inspect the final merged Android manifest/APK and reject `android.permission.INTERNET` before shipping. Until that final-package gate exists, the current validator is a source/project-level invariant rather than a complete binary proof.
