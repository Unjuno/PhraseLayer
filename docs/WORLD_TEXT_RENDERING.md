# World text rendering and source-mask contract

PhraseLayer keeps physical registration, source masking, and translated typography as separate stages so each can fail closed.

```text
WorldTextTrackingPlan
        ├──→ WorldTextMaskPolicy
        │          ↓
        │    UnityWorldTextSourceMaskBehaviour
        │
        └──→ UnityWorldTextRendererBehaviour
```

## Japanese font policy

A Japanese-capable Unity `Font` must be assigned explicitly through the scene/runtime setup or `SetFont`.

PhraseLayer does not commit or silently download a font binary. The self-hosted Quest fixture accepts a locally reviewed `.ttf` or `.otf` through `PHRASELAYER_JAPANESE_FONT_SOURCE`, copies it only into the git-ignored `Assets/LocalReadModeAssets` directory, and records the file name, byte size, and SHA-256 in local evidence before building.

If no font is assigned, `UnityWorldTextRendererBehaviour.TryPresent` records the supplied tracking plan but returns `false` and creates no text objects. The Quest end-to-end smoke refuses to pass in this state.

## Translated world-text placement

For each active stabilized track, `UnityWorldTextRendererBehaviour`:

- uses the translated `DisplayText` from the tracked assistance segment;
- uses the stabilized `WorldTextSurface.Center` and orientation basis;
- sizes text from the fitted physical text height;
- offsets the text slightly toward the camera along the center camera ray to reduce z-fighting;
- may keep short retained text tracks visible to bridge brief OCR/raycast dropouts;
- removes the Unity text object when the Core tracking plan expires the track.

The default surface offset is `0.003 m` and the default text-height fraction is `0.80`. These are implementation defaults, not Quest-validated perceptual settings.

`TextMesh` is intentionally used as a minimal Unity-native reference renderer. Production typography may later move behind the same tracking contract after Japanese glyph metrics, batching, readability, and Quest performance are measured.

## Conservative source-mask policy

Source masking is implemented separately from text rendering. It is deliberately stricter than merely retaining a world-text track.

Core `WorldTextMaskPolicy` requires, at minimum:

- an `InPlaceReplacement` target rather than an adjacent label;
- a translated display segment that actually differs from the source;
- the track to be **observed in the current frame**;
- repeated observation before masking;
- fitted planarity error within the configured mask threshold.

A retained track may remain useful for text continuity, but it is not allowed to keep hiding a physical area after the current observation disappears. This avoids turning temporal retention into stale real-world occlusion.

`UnityWorldTextSourceMaskBehaviour` then renders only the eligible mask set.

### No collider self-interference

The mask is a dedicated procedural quad mesh and does not create a `Collider`. This is intentional: a mask primitive with a collider could be hit by later surface raycasts and make PhraseLayer register future text against its own overlay instead of the environment.

The committed `PhraseLayer/SourceMask` shader is opaque, depth-writing, and double-sided. A local Material is generated from that shader during the fixture setup.

## Current mask is a geometry gate, not background reconstruction

The current fixture mask is an opaque solid surface used to verify:

- the correct OCR semantic region is selected;
- the fitted world plane is stable enough to cover it;
- the mask follows current observations;
- the translated Japanese text can occupy the same physical envelope.

It does **not** reconstruct the original wall/sign/menu background and must not be described as seamless text removal. Color/texture reconstruction, passthrough compositing strategy, stereo comfort, edge treatment, and neighboring-content preservation remain visual-quality gates.

## Current Quest integration

The generated fixture scene wires:

```text
Meta Passthrough Camera
        ↓
PP-OCR
        ↓
adaptive semantic assistance
        ↓
semantic ↔ OCR geometry
        ↓
MRUK live-depth four-corner physical surface fit
        ↓
temporal world-text tracking
        ↓
Core mask eligibility
        ├──→ collider-free source mask
        └──→ Japanese world text
```

`QuestReadModeSmokeTestBehaviour` does not accept OCR-only success. PASS requires:

- real camera/OCR smoke PASS;
- a newer adaptive Read Mode result;
- MRUK environment raycast ABI active;
- at least one layout-ready physical text surface;
- at least one currently observed track;
- source-mask render success and an active mask;
- translated world-text render success and an active text view;
- reviewed local Japanese font and mask Material configured.

Recognized source text and translated text are redacted from the default device evidence. Geometry/count diagnostics such as layout failures, active mask count, MRUK status/normal confidence, and maximum observed planarity error remain visible.

## Remaining validation

Before source masking can be treated as product-quality in-place replacement, real Quest 3 measurements still need to establish:

- Japanese glyph availability and readability in the Android build;
- physical registration error under head motion and changing viewing distance;
- stereo alignment/comfort;
- mask edge behavior and whether neighboring physical content is obscured;
- an acceptable background reconstruction/compositing strategy rather than the current solid fixture mask;
- renderer/mask batching, frame-time, memory, thermal, and battery impact;
- hardware camera timestamp / pose / depth synchronization.

The fixture exists to make those failures measurable; its existence is not evidence that those measurements already passed.
