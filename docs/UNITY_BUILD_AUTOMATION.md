# Unity Build Automation

PhraseLayer uses Unity Build Automation (UBA) as the reference cloud compile/build gate for the Unity/Quest shell. UBA is development-time CI only; the shipped PhraseLayer runtime remains local-only.

## Build target

- Name: `PhraseLayer Quest CI`
- Platform: `Android`
- Branch: `agent/multi-sentence-segmentation`
- Project subfolder: `unity/PhraseLayer.Unity`
- Unity version: auto-detect, expected `6000.0.66f2`
- Android SDK: `SDK 36`
- Development output: APK
- Scheduling: none required
- Auto-build: enabled

## Host compile preflight

GitHub Core CI compiles the Unity shell before relying on UBA. Two profiles are used because Unity Editor compilation and Android Player compilation expose different conditional branches.

### Editor profile

`PhraseLayer.UnityShell.Compile.csproj` enables:

- `UNITY_5_3_OR_NEWER`
- `UNITY_EDITOR`
- `PHRASELAYER_UNITY_AI_INFERENCE_2_2`

It compiles both `Assets/Scripts/**/*.cs` and `Assets/Editor/**/*.cs`. This catches Editor verification, Pre-Export hooks, local OCR asset tooling, and Unity Inference guarded code.

### Android Player profile

`PhraseLayer.UnityAndroid.Compile.csproj` enables:

- `UNITY_5_3_OR_NEWER`
- `UNITY_ANDROID`
- `PHRASELAYER_UNITY_AI_INFERENCE_2_2`

It deliberately does **not** define `UNITY_EDITOR` and compiles only `Assets/Scripts/**/*.cs`. This exposes Quest/Android-only code such as runtime camera permission handling that would otherwise stay hidden in an Editor-only host compile.

The two projects keep separate `obj/` and `bin/` roots so restore/build artifacts cannot overwrite one another.

Both profiles use narrow Unity/Inference compile stubs. They are not substitutes for the real Unity compiler: stubs cannot prove package import, source-generated APIs, graphics behavior, Meta XR compatibility, Android export, or device execution. They exist to reject ordinary C# breakage, missing project references, and guarded-branch drift before spending a UBA build.

Repository-side checks:

```bash
python tools/validate_unity_compile_preflight.py
dotnet build tests/PhraseLayer.UnityShell.Compile/PhraseLayer.UnityShell.Compile.csproj -c Release
dotnet build tests/PhraseLayer.UnityShell.Compile/PhraseLayer.UnityAndroid.Compile.csproj -c Release
```

## Committed Read MVP scene

`Assets/Scenes/PhraseLayerReadMvp.unity` is the enabled repository scene. It deliberately contains no PP-OCR or translation model weights.

The committed baseline serializes the MRUK `Meta.XR.PassthroughCameraAccess` component from the pinned `com.meta.xr.mrutilitykit@85.0.0` package and a normal Unity camera. `PhraseLayerReadMvpRuntimeInstaller` creates the PhraseLayer Read graph after scene load with the synthetic OCR fixture and dictionary translation fallback. This gives cloud builds a deterministic, model-free scene while keeping the shipped architecture local-only.

For Quest device work, stage the reviewed local PP-OCR assets and run:

`PhraseLayer -> Read MVP -> Create or Reset Local Read Scene`

That command replaces the baseline scene locally with the real camera bridge, PP-OCR bootstrap, learner profile, Read assistance, and—when the complete verified bundle is present—the local OPUS-MT bootstrap. Those model assets remain git-ignored and are never downloaded by the Editor command.

## Required Pre-Export hook

Set **Advanced Settings -> Pre-Export Method** to exactly:

`PhraseLayer.Unity.Editor.PhraseLayerCloudBuildVerification.PreExport`

UBA runs this method after Unity script compilation and before the player build. PhraseLayer uses that boundary to:

1. apply local-only Android defaults,
2. retain the committed Read MVP scene (or recover a deterministic shell only if no enabled scene exists),
3. run the local-only contract,
4. run the Unity language/OCR/runtime verification.

The repository also implements `IPreprocessBuildWithReport`. That second gate runs immediately before the player build and fails closed if the Pre-Export preparation did not establish an enabled scene or if any reviewed invariant is broken.

```text
Git push
  -> GitHub Editor compile preflight
  -> GitHub Android Player compile preflight
  -> UBA checkout/import
  -> real Unity C# compile
  -> PhraseLayerCloudBuildVerification.PreExport
       -> retain committed Read MVP build scene
       -> local-only verification
       -> PhraseLayer Unity verification
  -> IPreprocessBuildWithReport fail-closed gate
  -> Android player build
  -> PASS / FAIL
```

## Local Core package path

Unity resolves `file:` package dependencies relative to the project's `Packages` directory. Because this Unity project lives at `unity/PhraseLayer.Unity`, the Core package at repository root `src/PhraseLayer.Core` is referenced as:

`file:../../../src/PhraseLayer.Core`

Using `file:../../src/PhraseLayer.Core` incorrectly resolves to `unity/src/PhraseLayer.Core` and causes a clean cloud import to fail before compilation.

## Meta package resolution in cloud CI

`Packages/manifest.json` pins Meta MRUK `85.0.0` and uses the **standard Unity Package Manager resolution** path. Do not add a custom Meta scoped registry for this package unless an upstream Meta/Unity requirement explicitly changes.

Meta's current Passthrough Camera API sample also declares `com.meta.xr.mrutilitykit` directly in `Packages/manifest.json` without a custom `scopedRegistries` entry. Keeping PhraseLayer aligned with that package-resolution shape avoids introducing an unnecessary alternate registry/authentication failure mode in unattended UBA builds.

Package download during CI is a development/build-time network dependency only; it does not add runtime networking to the shipped Quest application.

## Build result visibility

The authoritative UBA result and log are in **Unity Dashboard -> DevOps -> Build Automation -> Build history -> Logs**. A manual or automatic UBA build is not guaranteed to create a GitHub commit status. GitHub tooling can observe the UBA result only if Unity publishes that result back to GitHub.

Repository-side structural validation:

```bash
python tools/validate_unity_build_automation.py
```

## Credentials

Use UBA's native Unity Cloud entitlement and the Git repository PAT configured in Unity Dashboard. Do not put Unity Personal license files or Google sign-in credentials into repository secrets.
