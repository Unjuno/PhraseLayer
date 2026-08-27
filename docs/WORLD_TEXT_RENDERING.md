# World text rendering contract

PhraseLayer separates physical registration from source-text masking.

`UnityWorldTextRendererBehaviour` is the first non-destructive world-space renderer for stabilized Read Mode tracks:

```text
WorldTextTrackingPlan
        ↓
stable track id + WorldTextSurface
        ↓
UnityWorldTextRendererBehaviour
        ↓
Unity TextMesh per active track
```

## Font policy

A Japanese-capable Unity `Font` must be assigned explicitly through the scene/Inspector or `SetFont`.

PhraseLayer does not bundle, download, or silently substitute a font in this renderer. The font asset's redistribution/license and Quest glyph coverage must be reviewed separately before release packaging.

If no font is assigned, `TryPresent` records the supplied tracking plan but returns `false` and creates no text objects. This lets camera/OCR/tracking verification continue without pretending the rendering dependency is configured.

## Placement

For each active stabilized track, the renderer:

- uses the translated `DisplayText` from the tracked assistance segment;
- uses the stabilized `WorldTextSurface.Center` and orientation basis;
- sizes text from the fitted physical text height;
- offsets the text slightly toward the camera along the center camera ray to reduce z-fighting;
- keeps short retained tracks visible when configured, allowing the tracking layer to bridge brief OCR/raycast dropouts;
- removes the Unity text object when the Core tracking plan expires the track.

The default surface offset is `0.003 m` and the default text-height fraction is `0.80`. These are implementation defaults, not Quest-validated perceptual settings.

`TextMesh` is intentionally used as a minimal Unity-native reference surface so this gate does not introduce another package or bundled font dependency. Production typography may later move behind the same tracking contract to a different renderer after Japanese glyph metrics, batching, readability, and Quest performance are measured.

## Non-destructive policy

This renderer **does not erase, occlude, blur, or paint over the physical source text**. It only places translated world-space text on the fitted surface.

That separation is deliberate. In-place source covering requires additional evidence that:

- OCR coverage is spatially correct;
- the environment/depth surface is registered correctly;
- the mask follows the source phrase under motion;
- masking does not cover neighboring physical content;
- visual artifacts are acceptable under stereo passthrough.

Until those conditions are validated on Quest 3, a text renderer must not imply that source masking is solved.

## Current integration

The generated demo scene wires:

```text
Meta Passthrough Camera
        ↓
PP-OCR
        ↓
adaptive semantic assistance
        ↓
semantic ↔ OCR geometry
        ↓
four-corner physical surface fit
        ↓
temporal world-text tracking
        ↓
UnityWorldTextRendererBehaviour
```

The final renderer remains inactive until a reviewed font is assigned.

## Remaining validation

- assign and license-review a Japanese-capable font;
- verify glyph availability on Android/Quest builds;
- measure physical alignment error and readability on Quest 3;
- validate TextMesh material/culling behavior under the actual XR render pipeline;
- measure renderer batching, frame-time, and memory impact;
- implement and separately validate source-text masking/occlusion;
- replace heuristic text-height sizing with measured glyph/bounds fitting if required by real fixtures.
