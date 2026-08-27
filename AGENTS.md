# AGENTS.md

## Product invariants

1. Never implement character-percentage translation.
2. Core output must not add brackets, arrows, parentheses, or gloss markers.
3. Assistance operates on semantic spans (word, MWE, phrase, clause, sentence).
4. Prefer preserving source text when estimated understanding is high.
5. Freeze an assistance plan for the lifetime of an encounter; recompute on a later encounter.
6. OCR, ASR, translation, camera, and XR runtimes remain replaceable behind interfaces.
7. `PhraseLayer.Core` must not depend on UnityEngine, Meta XR, Oculus, Android, networking SDKs, or concrete model runtimes.
8. No cloud dependency is mandatory for the core path.
9. Do not commit model weights before redistribution licenses are reviewed and recorded.
10. Hardware performance claims require Quest 3 measurements.
11. Preserve the caller synchronization context across platform-adapter awaits unless the adapter contract explicitly proves thread independence. Unity camera, graphics, and inference adapters may be owner-thread-bound.
12. Keep Meta SDK types behind the Unity adapter boundary; SDK drift must fail loudly at that boundary rather than leaking into Core.

## Current gate

Gate 0–3 are implemented. Gate 4/5 integration is active:

- Unity 6 shell exists and host compile validation is required.
- Meta MRUK/OpenXR packages are pinned in the Unity project.
- `MetaPassthroughCameraBridge` and a PP-OCR Unity Inference Engine path exist.
- Demo-scene tooling wires `Meta.XR.PassthroughCameraAccess` to the OCR runtime without introducing Meta compile-time types into Core.
- OCR viewport geometry and semantic-to-region alignment exist in Core.

Do **not** describe the camera/OCR path as Quest-verified until a real Quest 3 run records the required device evidence. The next product gates are real-device camera/OCR verification, stable world-space rendering/tracking, and offline English→Japanese NMT integration.

## Required tests for assistance changes

- at least one multiword expression;
- at least one clause-level replacement;
- no overlapping selected spans;
- punctuation and whitespace outside replaced spans are preserved;
- a known expression loses automatic assistance;
- a cached encounter does not flicker after learner state changes.

## Required tests for platform-adapter changes

- host Core tests remain green;
- Unity shell compile remains green;
- asynchronous platform boundaries do not move thread-affine adapters off their caller synchronization context;
- Meta/Unity SDK types do not enter `PhraseLayer.Core`;
- real-device behavior is reported as unverified unless measured on Quest 3.
