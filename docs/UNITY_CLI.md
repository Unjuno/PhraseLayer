# Unity CLI / container execution

Gate 3 is structured so the Unity Editor can verify the project without manual scene editing.

## Project

`unity/PhraseLayer.Unity`

The Unity project consumes the existing Core directly as a local UPM package:

```text
unity/PhraseLayer.Unity
  -> file:../../src/PhraseLayer.Core
```

`PhraseLayer.Core` remains the single source of truth; it is not copied into `Assets`. .NET build intermediates are redirected to the repository-level `artifacts/` directory so `obj/` generated C# files do not appear inside the local Unity package.

## Verification layers

The normal `Core CI` performs two hardware-independent checks:

1. builds/tests `PhraseLayer.Core`;
2. compiles the Unity runtime/editor source files against intentionally minimal Unity API stubs in `tests/PhraseLayer.UnityShell.Compile`.

The stub compile is only a fast structural check. It does not prove Unity package resolution, actual Unity API compatibility, scene serialization, rendering, or runtime behavior.

## Real Unity CLI verification

Set `UNITY_EDITOR` to an installed Unity Editor executable, then run:

```bash
./tools/unity/verify.sh
```

On PowerShell:

```powershell
$env:UNITY_EDITOR = "C:\\Program Files\\Unity\\Hub\\Editor\\6000.0.51f1\\Editor\\Unity.exe"
./tools/unity/verify.ps1
```

The command opens the pinned project in batch mode, compiles it, resolves the local Core package, and executes `PhraseLayerEditorVerification.VerifyCorePipelineBatch`. A successful run must print a Gate 3 PASS message and return exit code 0.

To generate the deterministic demo scene from source:

```bash
./tools/unity/create-demo-scene.sh
```

Then open `Assets/Scenes/PhraseLayerDemo.unity` and enter Play Mode. The demo exposes learner profiles and assistance modes while rendering the mixed English/Japanese result without bracket-style glosses.

## Self-hosted/container runner

`.github/workflows/unity-cli.yml` is an opt-in workflow for a runner labelled:

```text
self-hosted
unity
unity-6000-0-51f1
```

The runner may be a VM, physical machine, or container. It must expose a valid Unity Editor installation through `UNITY_EDITOR` and satisfy the Editor's activation/license requirements before the workflow starts. The workflow intentionally does not embed credentials or activation material.

## Container contract

A container can run the same verification command if it provides:

1. a compatible Unity Editor binary;
2. whatever activation/license state that Editor requires;
3. the repository mounted read/write;
4. `UNITY_EDITOR` pointing to the Editor executable inside the container.

The repository intentionally does not pin a third-party Unity image yet. An image should only be pinned after its provenance, supported Editor revision, module set, and licensing/activation path are reviewed.

The current ChatGPT execution container has neither Unity Editor nor Docker/Podman, so real Unity CLI execution cannot be performed inside this session. Host-side Core and Unity-shell compile checks remain available through GitHub Actions.
