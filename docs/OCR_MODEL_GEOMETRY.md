# OCR model geometry contract

Gate 4 needs detector boxes to land back in the camera frame exactly enough for later AR placement. The OCR backend must therefore keep three coordinate spaces distinct:

1. **source image space** — camera pixels, top-left origin, `ImagePoint` / `ImageQuad`;
2. **model input space** — detector tensor pixels after aspect-preserving resize and centered letterbox, `OcrModelPoint` / `OcrModelQuad`;
3. **viewport space** — normalized bottom-left coordinates used by viewport-to-ray projection, handled later by `ImageCoordinateMapper`.

`OcrLetterboxTransform` is the single source of truth for source↔model geometry. A detector backend should create one transform for each source frame and detector input size, preprocess the texture using the same scale/padding, and map detector quads back with that same transform before creating `OcrRegion` objects.

For a 1280×960 source frame and a 640×640 detector input, the transform is:

- scale: `0.5`
- resized content: `640×480`
- horizontal padding: `0`
- vertical padding: `80` model pixels on each side

A source point `(x, y)` therefore becomes `(0.5x, 0.5y + 80)` in detector input space. The inverse mapping removes the padding first and then divides by the scale.

## Backend contract

A production backend may use PP-OCR or another detector/recognizer, but it should expose outputs through the existing platform-neutral contracts:

- detector: `OcrDetectionCandidate` with a model-space quad and detector score;
- recognizer: `OcrRecognitionCandidate` with decoded text and recognizer score;
- mapper: `OcrModelOutputMapper.ToSourceRegion(...)` returns a source-space `OcrRegion`.

The mapped region confidence currently uses `min(detectorScore, recognizerScore)`. This is deliberately conservative and must not be treated as a calibrated probability. If the selected OCR model later provides a calibrated joint score, the policy can be replaced without changing spatial geometry.

## Non-negotiable implementation rule

The preprocessing implementation and inverse geometry must share the same scale and padding values. Do not independently recompute or round detector coordinates in a second code path. Any integer rounding needed by GPU textures should happen at the texture/preprocessing boundary while the geometry transform retains double precision.

## Device validation still required

This contract is host-tested only. Gate 4 still requires a real Unity/Quest run to verify the actual detector preprocessing, model tensor conventions, camera texture orientation, latency, memory, and OCR accuracy before the issue can close.
