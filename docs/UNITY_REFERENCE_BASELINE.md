# Unity reference baseline

PhraseLayer's Quest/Unity integration is validated against known-good public projects instead of a hand-grown minimal Unity shell.

## Primary compatibility reference

Repository: `oculus-samples/Unity-PassthroughCameraApiSamples`

Reviewed tree: `9105be64da8690b41154baf5629cb82dc2dbe4a7`

Why this is the primary reference:

- maintained by Meta as the Passthrough Camera API Unity sample;
- uses Quest 3 / Quest 3S passthrough camera input;
- contains an on-device Unity Inference Engine sample;
- uses the same Unity editor line and the same key package versions as PhraseLayer.

Reviewed compatibility pins:

| Component | Reference | PhraseLayer |
| --- | --- | --- |
| Unity | `6000.0.66f2` | `6000.0.66f2` |
| MRUK | `85.0.0` | `85.0.0` |
| Unity Inference Engine | `2.2.1` | `2.2.1` |
| XR Management | `4.5.4` | `4.5.4` |
| OpenXR | `1.15.1` | `1.15.1` |
| UGUI | `2.0.0` | `2.0.0` |

PhraseLayer intentionally keeps one additional package:

- `com.unjuno.phraselayer.core`: local platform-neutral Core package.

PhraseLayer intentionally does **not** adopt the reference project's runtime networking / analytics built-in modules. The official PhraseLayer distribution remains local-only.

## Serialized XR baseline

Because the Unity/MRUK/XR package pins above are identical, PhraseLayer commits the reviewed sample's generated XR configuration assets byte-for-byte rather than reconstructing undocumented YAML by hand:

- `Assets/XR/Loaders/OpenXRLoader.asset`;
- `Assets/XR/XRGeneralSettingsPerBuildTarget.asset`;
- `Assets/XR/Settings/OpenXR Editor Settings.asset`;
- `Assets/XR/Settings/OpenXR Package Settings.asset`;
- their folder/asset `.meta` files.

`tools/validate_unity_reference_baseline.py` computes each file's Git blob SHA and requires it to equal the reviewed Meta-reference blob. This is an integrity/equality check, not a cryptographic trust claim.

`ProjectSettings/EditorBuildSettings.asset` keeps PhraseLayer's own Read MVP scene, but its XR config-object entries use the reviewed settings GUIDs:

- `Unity.XR.Oculus.Settings` -> `f2bf97b3acdb64248a707c407c9fc54e`;
- `com.unity.xr.management.loader_settings` -> `a971eac5e950046e586c5e153e32d05c`;
- `com.unity.xr.openxr.settings4` -> `9165b3c3dec8d446f9b11d1a99b6e245`.

The validator still rejects arbitrary additional scene/config GUIDs and Meta sample scene identities. The Android OpenXR settings must retain the enabled Meta XR feature and Oculus Touch controller profile.

## Secondary release-engineering reference

Repository: `Uralstech/UXR.QuestCamera`

Distribution: OpenUPM package `com.uralstech.uxr.questcamera`.

Reviewed stable release: `4.2.3`.

This project is not the exact runtime compatibility baseline because it has moved to newer Unity / Inference versions. It is used as a reference for:

- reusable UPM packaging;
- versioned public releases;
- documentation and installability;
- maintaining a Quest camera integration as a library rather than a one-off scene.

## Migration doctrine

Do not fix PhraseLayer by guessing individual Unity or Meta API signatures first.

Use this order:

1. reproduce the primary reference's package and Unity project structure;
2. prove an empty/minimal Quest player build;
3. prove passthrough camera access;
4. prove a minimal Inference Engine invocation using the same public API pattern as the reference;
5. add PP-OCR detector;
6. add PP-OCR recognizer;
7. reconnect semantic assistance, learner state, and MR presentation.

When PhraseLayer differs from the primary reference, the difference must be either:

- required by PhraseLayer's product behavior; or
- an explicit local-only/privacy constraint.

Avoid architecture-only differences until the reference-equivalent vertical slice builds successfully.

## Current structural baseline

The earlier skeletal-project gap is now materially reduced. PhraseLayer commits deterministic `ProjectSettings`, the reviewed XR loader/settings assets, an enabled `Assets/Scenes/PhraseLayerReadMvp.unity`, and the scene's stable `.meta` GUID. The committed scene remains model-free: it serializes the reviewed MRUK 85 `PassthroughCameraAccess` component and PhraseLayer runtime code installs a synthetic-fixture Read graph after scene load.

This is a build/import baseline, not evidence of Quest device correctness. The following remain separate gates:

- real Unity/UBA import and Android player build of the committed scene;
- explicit head-tracked camera behavior;
- Quest 3 passthrough camera startup and permission behavior;
- locally staged PP-OCR execution;
- local OPUS-MT execution;
- device performance and thermal measurements.

Local PP-OCR and translation model files remain git-ignored. A device-test scene with those local assets is generated explicitly through `PhraseLayer -> Read MVP -> Create or Reset Local Read Scene`; cloud CI must not manufacture or download model weights.
