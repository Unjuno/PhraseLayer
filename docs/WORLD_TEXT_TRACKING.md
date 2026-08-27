# World text temporal tracking

`WorldTextLayoutPlanner` produces a geometry-valid physical text surface for one OCR observation. Rendering that raw result directly is still unstable because OCR polygons and environment surface hits move slightly between observations.

`WorldTextTrackStabilizer` adds a platform-neutral temporal layer after four-corner fitting:

```text
WorldTextLayoutPlan
        ↓
phrase + spatial-neighborhood association
        ↓
WorldTextTrackStabilizer
        ↓
WorldTextTrackingPlan
  - stable track id
  - smoothed metric surface
  - observed vs retained state
  - first/last observation timestamps
  - observation count
```

## Association policy

A current layout target may reuse an existing track only when:

1. both source text and translated display text match after case/whitespace normalization;
2. the prior stabilized center is within the configured world-space association distance;
3. that existing track has not already been claimed by another target in the same update.

The default association radius is `0.15 m`. This is an implementation default, not a Quest-validated threshold.

Including translated display text in the association key is deliberate. If learner adaptation changes the assistance translation on a later encounter, the previous visual track is not silently reused with new content.

Repeated identical phrases are separated by nearest world-space position. This is still a local association heuristic rather than a full visual feature tracker.

## Smoothing

Accepted surface centers, metric extents, and orientation axes are exponentially smoothed using elapsed observation time. The default time constant is `0.12 s`.

Axis signs are aligned to the previous track before interpolation, preventing equivalent basis sign changes from causing a visible 180-degree flip. The blended right/up axes are re-orthogonalized and the layout normal is reconstructed from them.

Only surfaces that already passed the four-corner geometry gate enter the stabilizer. Tracking never converts rejected geometry into valid geometry.

## Temporary observation loss

A track remains available for a short retention interval when the current observation does not contain it. The default retention is `0.60 s`.

After the retention interval expires, the track is deleted **before** association. A phrase that reappears later therefore receives a new track identity instead of reviving stale pose history.

This retained state is intended to support brief OCR/raycast dropouts. It is not an anchor or SLAM guarantee.

## Timestamp contract

Tracking timestamps must be monotonic and use the same microsecond time base for one stabilizer instance. Backward timestamps are rejected.

The current Meta camera bridge still uses local Unity monotonic observation time rather than a verified camera hardware timestamp. Consequently, temporal tracking does not yet prove camera-pose/depth synchronization on Quest.

## Unity bridge

`UnityWorldTextTrackingBehaviour` owns the Core stabilizer and can execute:

```text
ReadModeAlignedResult
        ↓
UnitySpatialProjectionBehaviour
        ↓
WorldTextLayoutPlan
        ↓
WorldTextTrackStabilizer
        ↓
WorldTextTrackingPlan
```

The generated demo scene wires the tracking behaviour to `UnitySpatialProjectionBehaviour`.

## Still required

- a production Japanese world-space text renderer;
- source-text masking/occlusion policy;
- a reviewed environment depth/mesh source on Quest;
- association/error thresholds measured on real Quest camera fixtures;
- camera hardware timestamp / pose synchronization verification;
- longer-term anchoring if text must remain stable through large viewpoint changes or temporary disappearance.
