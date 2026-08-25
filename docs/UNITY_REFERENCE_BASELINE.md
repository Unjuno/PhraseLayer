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

The earlier skeletal-project gap is now partially closed. PhraseLayer commits deterministic `ProjectSettings`, an enabled `Assets/Scenes/PhraseLayerReadMvp.unity`, and the scene's stable `.meta` GUID. The committed scene is intentionally model-free: it serializes the reviewed MRUK 85 `PassthroughCameraAccess` component and a normal Unity camera, while PhraseLayer runtime code installs a synthetic-fixture Read graph after scene load.

This is a build/import baseline, not evidence of Quest device correctness. The following remain separate gates:

- real Unity/UBA import and Android player build of the committed scene;
- Quest 3 passthrough camera startup and permission behavior;
- locally staged PP-OCR execution;
- local OPUS-MT execution;
- head-tracked MR presentation and device performance measurements.

Local PP-OCR and translation model files remain git-ignored. A device-test scene with those local assets is generated explicitly through `PhraseLayer -> Read MVP -> Create or Reset Local Read Scene`; cloud CI must not manufacture or download model weights.
