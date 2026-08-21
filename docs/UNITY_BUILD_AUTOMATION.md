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

GitHub Core CI compiles the Unity shell before relying on UBA. The preflight project deliberately enables the same reviewed conditional branches that are active in the real Unity project:

- `UNITY_5_3_OR_NEWER`
- `UNITY_EDITOR`
- `PHRASELAYER_UNITY_AI_INFERENCE_2_2`

It includes both `Assets/Scripts/**/*.cs` and `Assets/Editor/**/*.cs`, with narrow Unity/Inference compile stubs. This is not a substitute for the real Unity compiler: the stubs cannot prove package import, source-generated APIs, graphics behavior, Meta XR compatibility, Android export, or device execution. It exists to reject ordinary C# breakage, missing project references, and guarded-branch drift before spending a UBA build.

Repository-side checks:

```bash
python tools/validate_unity_compile_preflight.py
dotnet build tests/PhraseLayer.UnityShell.Compile/PhraseLayer.UnityShell.Compile.csproj -c Release
```

## Required Pre-Export hook

Set **Advanced Settings -> Pre-Export Method** to exactly:

`PhraseLayer.Unity.Editor.PhraseLayerCloudBuildVerification.PreExport`

UBA runs this method after Unity script compilation and before the player build. PhraseLayer uses that boundary to:

1. apply local-only Android defaults,
2. create the deterministic shell scene if the repository does not yet have an enabled production scene,
3. run the local-only contract,
4. run the Unity language/OCR/runtime verification.

The repository also implements `IPreprocessBuildWithReport`. That second gate runs immediately before the player build and fails closed if the Pre-Export preparation did not establish an enabled scene or if any reviewed invariant is broken.

```text
Git push
  -> GitHub host compile preflight
  -> UBA checkout/import
  -> real Unity C# compile
  -> PhraseLayerCloudBuildVerification.PreExport
       -> establish shell/build scene
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

`Packages/manifest.json` pins Meta MRUK `85.0.0` and uses the **standard Unity Package Manager resolution path**. Do not add a custom Meta scoped registry for this package unless an upstream Meta/Unity requirement explicitly changes.

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
