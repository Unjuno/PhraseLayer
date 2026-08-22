# Unity compile isolation experiment

This document records the controlled diagnostic protocol for the Unity Build Automation compile failure.

## Goal

Identify the first real C# compiler diagnostic before treating Unity Build Automation's generic `Script Compiler Error` / `Unity Player Export Failure` summaries as root causes.

## Control run

This commit intentionally changes documentation only. The Unity/Core C# source and package manifest are unchanged from the immediately preceding branch head.

Measured outputs:

- `phraselayer/unity-preflight` commit status
- Unity Editor preflight exit code
- Android Player preflight exit code
- first `error CSxxxx` diagnostic emitted by either preflight profile
- corresponding GitHub Actions run URL
- UBA result, used only after host preflight passes or when the failure is proven Unity-specific

## Interpretation

- Editor=FAIL, Android=PASS: inspect Editor-only scripts/stubs/assembly references.
- Editor=PASS, Android=FAIL: inspect Android/Quest-only conditional branches.
- Editor=FAIL, Android=FAIL with the same diagnostic: inspect shared runtime/Core code or shared stubs.
- Host preflight=PASS but UBA script compile=FAIL: inspect real Unity/package API drift, asmdef resolution, package import, or Unity-specific compiler environment.

## Current controlled package baseline

- Unity: `6000.0.66f2`
- `com.unity.ai.inference`: `2.2.1`
- `com.meta.xr.mrutilitykit`: `85.0.0`
- `com.meta.xr.sdk.core`: `85.0.0`
- `com.unity.xr.meta-openxr`: `2.4.0`
- `com.unity.xr.openxr`: `1.15.1`

Do not infer product/runtime correctness from this compile experiment. It only isolates build-system and API-surface failures.
