# Spatial text alignment

PhraseLayer chooses assistance by semantic character spans, while OCR systems usually return word or line polygons. This layer connects the two without introducing Meta XR dependencies into Core.

## Pipeline

```text
OcrObservation.Text
+ OcrViewportRegion[]
        ↓
OcrRegionTextAligner
        ↓
OcrTextRegionSpan[]
  - source character span
  - viewport polygon
        ↓
MixedLanguagePlan
        ↓
SemanticRegionAligner
        ↓
SpatialAssistanceTarget[]
  - translated semantic segment
  - one or more OCR regions
  - Exact / Partial / Unresolved coverage
  - viewport envelope
```

## Token alignment

OCR region text is matched to the complete recognized source using case-insensitive lexical tokens. Punctuation differences therefore do not prevent `grass` from matching `grass.`. Repeated words are assigned to separate unclaimed source-token occurrences.

An unmatched OCR region remains explicitly unresolved; it is never attached to a semantic unit by guessing.

## Semantic coverage

For each assisted segment, every lexical token in its semantic span is checked against resolved OCR region spans:

- **Exact**: every token in the semantic unit has OCR geometry.
- **Partial**: at least one, but not all, tokens have geometry.
- **Unresolved**: no reliable geometry is available.

Example:

```text
source: Please keep off the grass.
semantic assistance: keep off → 立ち入らない
OCR boxes: [Please] [keep] [off] [the] [grass]
```

The spatial target joins the `keep` and `off` OCR regions and exposes their viewport envelope. If only the `keep` box exists, coverage is `Partial` rather than falsely treating the placement as exact.

## Rendering policy

Core does not decide how a translated phrase is painted over multiple physical word boxes. The Unity/Quest renderer can later choose among:

- one label anchored at the combined envelope center;
- a plane fitted from all OCR quad corners;
- a per-word mask with one translated replacement layer;
- a fallback nearby label when coverage is partial.

`Partial` and `Unresolved` should generally avoid destructive source-text covering until the Quest renderer has a confidence policy.

## Current limitation

The MVP aligner assumes `OcrObservation.Text` and region texts share the same lexical content. It does not yet perform fuzzy edit-distance alignment for OCR misspellings. That should be added only with fixtures demonstrating real Quest camera OCR failure patterns, so the matcher does not silently attach incorrect physical regions.
