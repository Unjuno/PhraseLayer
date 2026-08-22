# PhraseLayer build environment

PhraseLayer uses a two-layer build verification model.

## 1. GitHub preflight: fast and reproducible

GitHub Actions must catch ordinary C# errors before a Unity Build Automation (UBA) build is consumed. The repository therefore carries the complete *compile contract* needed for host-side verification:

- exact .NET SDK pin (`global.json`)
- Python toolchain major/minor pin
- C# language level fixed to Unity 6 compatibility (`9.0`)
- `netstandard2.1` compatibility for Unity-facing host compile projects
- explicit Unity/Android compiler defines
- checked-in Unity/Inference/Android compile stubs for only the API surface PhraseLayer uses
- Unity editor version and direct package versions in a machine-readable environment lock
- compiler logs and environment snapshots uploaded as GitHub Actions artifacts
- commit status `phraselayer/unity-preflight`, linked back to the exact Actions run

This layer is intentionally *not* treated as proof that Unity can build the project. Its purpose is to make common compiler failures cheap and immediately diagnosable.

## 2. Unity Build Automation: authoritative real-Unity gate

The authoritative gate remains Unity Build Automation using:

- Unity `6000.0.66f2`
- Android / Quest target
- Windows builder
- the checked-in Unity project under `unity/PhraseLayer.Unity`
- the reviewed Meta XR / OpenXR / Unity Inference package set

This is necessary because the project is developed under Unity Personal. Unity's current licensing documentation states that Unity Personal activation and return are handled through Unity Hub; command-line/manual activation is not supported for Personal. That makes a licensed real Unity Editor on an ephemeral GitHub-hosted runner an unsuitable reference CI design. UBA is therefore the real compiler/build authority, while GitHub provides the fast mirror.

References:

- Unity 6 licensing methods: https://docs.unity3d.com/Manual/LicenseActivationMethods.html
- Unity command-line license management: https://docs.unity3d.com/Manual/ManagingYourUnityLicense.html
- UBA REST API: https://docs.unity.com/en-us/build-automation/build-automation-api
- UBA pre-build scripts: https://docs.unity.com/en-us/build-automation/advanced-build-configuration/run-custom-scripts-during-the-build-process

## Package determinism

Unity Package Manager stores the resolved direct + transitive dependency graph in `Packages/packages-lock.json`. Unity documents that the lock file exists specifically to make package resolution deterministic and efficient.

`unity/PhraseLayer.Unity/Packages/packages-lock.json` is not yet committed because the repository has not yet produced a clean, reviewed Unity package resolution from the current package set. Do not fabricate this file. As soon as a real Unity/UBA import successfully resolves the package graph, capture that generated lock file, review it, and commit it. From that point onward CI should fail if the lock file drifts unexpectedly.

Reference: https://docs.unity3d.com/Manual/upm-conflicts-auto.html

## UBA environment capture

`tools/uba/capture-environment.sh` is intended for the UBA **Pre-Build Script** hook. It prints a stable `PHRASELAYER_UBA_ENV_*` block containing the build revision, Unity version, target, builder OS, Android SDK/NDK paths, project editor pin, direct package manifest, and any already-existing `packages-lock.json`.

This script runs before the Unity build process starts, which is the earliest UBA shell hook. It does not mutate the project and must never print credentials.

## Feedback loop

The intended iteration loop is:

```text
push
  -> GitHub deterministic preflight
       -> exact CSxxxx diagnostic + logs + environment artifact
  -> UBA real Unity build
       -> authoritative package/script/Android result
  -> fix
```

A later integration may poll the UBA REST API from GitHub Actions using Unity Service Account credentials stored only as GitHub Actions secrets. That can mirror UBA result/log metadata back into GitHub without operating a PhraseLayer server. No Unity credential or license file may be committed to this repository.
