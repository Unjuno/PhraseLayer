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

Controlled change:

- keep `com.meta.xr.mrutilitykit` at `85.0.0`;
- explicitly pin `com.meta.xr.sdk.core` at `85.0.0`;
- explicitly pin `com.unity.xr.meta-openxr` at `2.4.0`;
- keep generic `com.unity.xr.openxr` at `1.15.1`;
- do not change PhraseLayer runtime logic in this experiment.

Interpretation:

- real Unity compile improves/passes: keep the dependency baseline and investigate the next gate;
- the same script compiler failure remains: Meta dependency under-specification is not sufficient to explain the failure; continue isolation in PhraseLayer/Inference/Editor surfaces;
- package resolution fails earlier: revert only the experimental dependency addition and record the exact package error.

## Experiment B — diagnostic transport control

Hypothesis: the GitHub preflight can surface the exact compiler family and Actions run without changing PhraseLayer runtime code.

Controlled change:

- documentation-only commit;
- no Unity source, package, asmdef, model, or build-setting change;
- allow both GitHub CI and Unity Build Automation to run from the same commit.

Interpretation:

- `phraselayer/unity-editor-preflight` / `phraselayer/unity-android-preflight` expose a `CSxxxx` diagnostic: fix that host-reproducible defect before interpreting UBA;
- host preflight is green but UBA still reports Script Compiler Error: the residual defect is specific to real Unity/package assembly resolution and should be isolated there;
- status contains a run URL: GitHub MCP can traverse run -> job -> log directly, eliminating manual screenshot diagnosis for future experiments.

## Experiment C — exact status transport verification

Hypothesis: the current Core CI status bridge can expose the first concrete Editor/Android `error CSxxxx` for the exact branch head through GitHub commit statuses alone.

Controlled change:

- documentation-only trigger commit on 2026-08-23;
- no Unity source, package, asmdef, model, or build-setting change;
- preserve the Meta v85 dependency baseline and current Inference/translation implementation unchanged.

Interpretation:

- Editor/Android statuses are green: host preflight no longer explains the UBA failure; isolate real-Unity assembly/package compilation next;
- either status is red with `CSxxxx`: fix that exact host-reproducible compiler defect first;
- red status without `CSxxxx`: treat the preflight harness/environment itself as the failing boundary and inspect the linked Actions run.

## Preflight observability contract

GitHub Core CI publishes `phraselayer/unity-preflight` on every push. The status must contain a link to the exact Actions run and, on failure, the first `error CSxxxx` diagnostic when one is available. The associated artifact preserves Editor and Android compile logs plus the pinned environment snapshot.

This status is a host preflight only. A green status cannot replace a real Unity 6000.0.66f2 compile, but a red status should normally be fixed before spending another UBA build.
