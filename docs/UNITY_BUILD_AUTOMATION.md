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
  -> UBA checkout/import
  -> Unity C# compile
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

`Packages/manifest.json` pins Meta MRUK `85.0.0` and routes `com.meta.xr` packages through Meta's public package registry:

`https://npm.developer.oculus.com`

This avoids relying on an interactive Unity Asset Store entitlement flow on an unattended UBA builder. Package download is a development/build-time network dependency only; it does not add runtime networking to the shipped Quest application.

## Build result visibility

The authoritative UBA result and log are in **Unity Dashboard -> DevOps -> Build Automation -> Build history -> Logs**. A manual or automatic UBA build is not guaranteed to create a GitHub commit status. GitHub tooling can observe the UBA result only if Unity publishes that result back to GitHub.

Repository-side structural validation:

```bash
python tools/validate_unity_build_automation.py
```

## Credentials

Use UBA's native Unity Cloud entitlement and the Git repository PAT configured in Unity Dashboard. Do not put Unity Personal license files or Google sign-in credentials into repository secrets.
