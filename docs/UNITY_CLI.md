# Unity CLI / container execution

Gate 3 is structured so the Unity Editor can verify the project without manual scene editing.

## Project

`unity/PhraseLayer.Unity`

The Unity project consumes the existing Core directly as a local UPM package:

```text
unity/PhraseLayer.Unity
  -> file:../../src/PhraseLayer.Core
```

`PhraseLayer.Core` remains the single source of truth; it is not copied into `Assets`.

## CLI verification

Set `UNITY_EDITOR` to an installed Unity Editor executable, then run:

```bash
./tools/unity/verify.sh
```

On PowerShell:

```powershell
$env:UNITY_EDITOR = "C:\\Program Files\\Unity\\Hub\\Editor\\6000.0.51f1\\Editor\\Unity.exe"
./tools/unity/verify.ps1
```

The command compiles the Unity project and executes `PhraseLayerEditorVerification.VerifyCorePipelineBatch`. A successful run must print a Gate 3 PASS message and return exit code 0.

To generate the deterministic demo scene from source:

```bash
./tools/unity/create-demo-scene.sh
```

Then open `Assets/Scenes/PhraseLayerDemo.unity` and enter Play Mode. The demo exposes learner profiles and assistance modes while rendering the mixed English/Japanese result without bracket-style glosses.

## Container contract

A container can run the same command if it provides:

1. a compatible Unity Editor binary;
2. whatever activation/license state that Editor requires;
3. the repository mounted read/write;
4. `UNITY_EDITOR` pointing to the Editor executable inside the container.

This repository intentionally does not pin a third-party Unity image yet. The current ChatGPT execution container does not contain a Unity Editor binary, so Unity compilation cannot be executed here; GitHub host-side Core CI remains independent of Unity.
