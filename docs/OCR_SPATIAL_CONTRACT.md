# OCR spatial contract

PhraseLayer keeps OCR geometry independent of Unity and Meta XR.

## Coordinate conventions

OCR/detector output is represented as an `ImageQuad` in source-image pixel coordinates:

```text
origin: top-left
+X: right
+Y: down
```

Before a Quest-specific adapter asks the camera API for rays, Core converts every quad point to `ViewportPoint`:

```text
origin: bottom-left
U,V: normalized to [0,1]
```

The conversion is:

```text
U = X / imageWidth
V = 1 - Y / imageHeight
```

Detector overshoot is clamped at the viewport boundary. The four point order is preserved so rotated/perspective text is not reduced to an axis-aligned rectangle.

## Responsibility boundary

```text
Passthrough frame
    ↓
OCR runtime
    ↓
OcrObservation
  └─ OcrRegion[]
       └─ ImageQuad (top-left pixels)
    ↓
OcrViewportMapper
    ↓
OcrViewportRegion[]
       └─ ViewportQuad (bottom-left normalized)
       └─ Anchor = quad centroid
    ↓
Quest adapter
    ↓
PassthroughCameraAccess.ViewportPointToRay(...)
    ↓
depth/environment raycast
    ↓
world-space text surface / overlay tracking
```

Core deliberately does not implement camera intrinsics, extrinsics, Unity `Ray`, depth raycasts, or spatial anchors. Those belong to the Quest/Unity platform layer.

## Why quad instead of rectangle

Real text can be rotated and perspective-distorted. Lightweight OCR systems commonly return four-corner polygons. Keeping the quad permits later strategies such as:

- center ray for a cheap first placement;
- four corner rays for estimating the physical text plane;
- tracking the same polygon between OCR refreshes;
- fitting overlays to slanted signs rather than only screen-aligned labels.

## Meta reference behavior

The current Meta `Unity-PassthroughCameraApiSamples` CameraToWorld sample uses `PassthroughCameraAccess.GetCameraPose()` and `ViewportPointToRay()` rather than assuming the image center is exactly the optical axis. PhraseLayer follows the same architectural rule: Core supplies viewport points; the Meta adapter asks the camera API for the actual ray.

No Meta sample source code is copied into Core. Any later copied/adapted code must retain its applicable upstream license notice.
