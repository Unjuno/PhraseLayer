# Meta Quest development baseline

This file records the upstream Meta sample versions PhraseLayer uses as the starting point for Quest-specific work. It is intentionally descriptive: Gate 3 remains Meta-free, while Gate 4 may adopt only the packages it actually needs.

## Upstream reference

Repository: `oculus-samples/Unity-PassthroughCameraApiSamples`

Observed on the upstream `main` branch during PhraseLayer development:

```text
Unity Editor: 6000.0.66f2
Editor revision: b20bc5da3050
```

Relevant package pins in the upstream sample:

```text
com.meta.xr.mrutilitykit        85.0.0
com.unity.ai.inference          2.2.1
com.unity.xr.management         4.5.4
com.unity.xr.openxr             1.15.1
com.unity.mobile.android-logcat 1.4.7
com.unity.ugui                  2.0.0
```

## PhraseLayer policy

- Gate 3 pins Unity to the same editor revision so Editor-shell validation is performed against the same baseline as the current Meta Passthrough Camera sample.
- Gate 3 does **not** add Meta XR packages.
- Gate 4 will add the minimum required Meta/OpenXR packages after the real Unity Editor resolves the Gate 3 project successfully.
- Package versions must not be described as hardware-verified until a Quest 3 APK has been built and tested.
- Upstream version drift is expected. Any upgrade should record the upstream commit or observed revision and rerun Unity CLI + Quest benchmarks.
