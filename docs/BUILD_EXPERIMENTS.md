# PhraseLayer build experiments

This document records controlled build-isolation experiments for the Unity 6000.0.66f2 / Quest CI baseline. The purpose is diagnosis, not product behavior. Change one dependency boundary at a time, keep the branch buildable, and interpret only the newest commit's results.

## Diagnostic hierarchy

1. GitHub `phraselayer/unity-preflight` status: host C# Editor/Android guarded-branch compile.
2. Unity Build Automation `Script Compiler Error`: authoritative real-Unity compilation.
3. Unity `Player Export Failure`: secondary when script compilation already failed.
4. Quest device smoke tests: runtime/package/device behavior after cloud compilation is green.

A UBA `Player Export Failure` following a `Script Compiler Error` is not counted as an independent defect.

## Experiment A — Meta v85 dependency baseline

Hypothesis: the minimal MRUK-only manifest under-specifies the reviewed Quest/OpenXR dependency surface for Unity 6000.0.

Initial controlled change:

- keep `com.meta.xr.mrutilitykit` at `85.0.0`;
- explicitly pin `com.meta.xr.sdk.core` at `85.0.0`;
- explicitly pin `com.unity.xr.meta-openxr` at `2.4.0`;
- keep generic `com.unity.xr.openxr` at `1.15.1`;
- do not change PhraseLayer runtime logic in this experiment.

Result: **hypothesis not supported; explicit additions reverted.**

The reviewed Meta Passthrough Camera API sample at `9105be64da8690b41154baf5629cb82dc2dbe4a7` does not explicitly declare either `com.meta.xr.sdk.core` or `com.unity.xr.meta-openxr`. PhraseLayer now follows that proven direct-package surface, with only the local `com.unjuno.phraselayer.core` addition and deliberate removal of Unity analytics / UnityWebRequest built-in modules for the official local-only distribution.

The experiment also exposed a CI-environment-lock drift. `Packages/manifest.json` had moved to the reviewed Meta reference baseline while `ci/unity-environment.lock.json` still described the earlier experimental package set. Commit `84b6096b0aff3588a69303e466579466eceb51b8` synchronized the lock to the reviewed manifest.

Interpretation:

- do not re-add explicit Meta Core / Meta OpenXR dependencies merely to address a generic UBA compiler error;
- use the exact Meta PCA direct-package baseline first;
- treat future package divergence as an explicit migration with evidence.

## Experiment B — diagnostic transport control

Hypothesis: the GitHub preflight can surface the exact compiler family and Actions run without changing PhraseLayer runtime code.

Controlled change:

- no product-behavior dependency on the transport mechanism;
- publish Editor and Android host-preflight statuses for each push;
- preserve exact Actions run identity in a status context;
- store compiler logs and the pinned environment snapshot as CI artifacts.

Result: **PASS.**

GitHub MCP can now traverse the status context to the exact Core CI run ID, then fetch run jobs and decoded job logs. This removed the need to diagnose ordinary host-preflight failures from Unity Dashboard screenshots.

## Experiment C — exact status transport verification

Hypothesis: the current Core CI status bridge can expose whether the exact branch head failed in Editor preflight, Android preflight, or before compilation.

Observed sequence:

1. Run `32643258977` failed before compilation in `validate_build_environment.py` with:
   `Packages/manifest.json direct dependencies drifted from ci/unity-environment.lock.json`.
2. The environment lock was synchronized with the reviewed manifest at `84b6096b0aff3588a69303e466579466eceb51b8`.
3. Run `32643368243` then passed both Unity Editor and Android host C# preflight, and the complete Core job passed.
4. After the translation-quality gate additions, run `32643686242` again passed Core, Unity Editor preflight, and Unity Android preflight.

Result: **PASS.**

The current host-reproducible boundary is green. A subsequent UBA `Script Compiler Error` therefore belongs to real Unity/package assembly resolution or another Unity-only surface until contrary evidence appears.

## Preflight observability contract

GitHub Core CI publishes:

- `phraselayer/unity-editor-preflight`;
- `phraselayer/unity-android-preflight`;
- `phraselayer/unity-preflight`;
- `phraselayer/unity-run-<run-id>`.

On failure, the workflow extracts the first concrete `error CSxxxx` diagnostic when one exists. It also uploads Editor/Android compile logs and a pinned environment snapshot.

This status is a host preflight only. A green status cannot replace a real Unity 6000.0.66f2 compile, but a red status must normally be fixed before spending another UBA build.
