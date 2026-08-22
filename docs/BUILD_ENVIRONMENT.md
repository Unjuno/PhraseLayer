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

`tools/uba/capture-environment.sh` is intended for the UBA **Pre-Build Script** hook. Build Automation requires the script to be committed to source control and configured by a path relative to the repository root. Set the build target's **Advanced Settings -> Pre-Build Script** to:

```text
tools/uba/capture-environment.sh
```

The script prints only reviewed, non-secret build metadata between `PHRASELAYER_UBA_ENV_BEGIN` and `PHRASELAYER_UBA_ENV_END`, including Unity version, Git revision/branch, build number/target, builder OS, Android SDK/NDK paths, project editor pin, direct package manifest, and any already-existing `packages-lock.json`. It does not dump the complete environment and therefore does not leak credentials.

## GitHub -> UBA result feedback

`.github/workflows/uba-feedback.yml` mirrors the authoritative UBA result back to the same Git commit. It polls at 30-second intervals (well below the API's 100 requests/minute limit), matches the UBA build by Git revision, downloads categorized failures and the full log after a failure, extracts concrete `error CSxxxx` diagnostics first, uploads the raw diagnostics as a GitHub artifact, and publishes commit status `phraselayer/uba`.

The workflow is inert until these GitHub repository settings are added:

- Secret `UNITY_UBA_API_KEY` — from Unity Dashboard -> DevOps -> Build Automation -> Settings. Never commit this value.
- Variable `UNITY_UBA_PROJECT_ID` — the Unity Cloud project GUID.
- Optional variable `UNITY_UBA_ORG_ID` — organization foreign key. If omitted, the client attempts to discover it from `/projects`.
- Optional variable `UNITY_UBA_BUILD_TARGET` — build target ID or exact name (for example `PhraseLayer Quest CI`). If omitted, the client selects the target for the current branch when unambiguous.

The API uses `Authorization: Basic <API key>`. `tools/uba/sync_build_status.py` uses only Python's standard library and never prints the API key. If a newer Git push supersedes the previous one, GitHub concurrency cancels the stale polling job, matching UBA's auto-cancel behavior.

## Feedback loop

The intended iteration loop is:

```text
push
  -> GitHub deterministic preflight
       -> exact CSxxxx diagnostic + logs + environment artifact
       -> phraselayer/unity-preflight
  -> UBA real Unity build
       -> authoritative package/script/Android result
  -> GitHub UBA feedback
       -> failure categories + full log artifact
       -> phraselayer/uba with concrete CSxxxx when available
  -> fix
```

No PhraseLayer server is required for this loop. GitHub Actions talks directly to the Unity Build Automation REST API using the repository secret, and no Unity credential or license file is committed to the repository.
