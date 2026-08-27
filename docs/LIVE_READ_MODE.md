# Live Read Mode runtime

The camera/OCR pump publishes each successful `(OcrObservation, ImageFrame)` pair through `OcrViewportDebugBehaviour.ObservationPresented`.

`UnityLiveReadModeBehaviour` subscribes to that already-recognized stream. It does **not** call OCR again.

```text
camera frame
   ↓
PP-OCR (once)
   ↓
ObservationPresented
   ↓
LiveReadModeCoordinator
   ↓
ReadModeObservationProcessor
   ↓
adaptive mixed-language plan
   ↓
semantic ↔ OCR geometry
   ↓
four-corner surface fit
   ↓
temporal track stabilization
   ↓
font-injected world-space renderer
```

## Latest-observation-wins policy

Live camera input can arrive faster than translation/alignment completes. A naïve pipeline can therefore display an older semantic result after a newer camera frame has already been processed.

`LiveReadModeCoordinator` prevents that failure mode with two independent gates:

1. a newer accepted frame cancels the older in-flight processing token;
2. every submission receives a generation number, and an older generation is rejected as `Superseded` even if an adapter ignores cancellation and completes later.

A frame timestamp that is less than or equal to the latest accepted timestamp is rejected immediately as `StaleInput` before language processing.

External lifecycle cancellation remains distinct from supersession and propagates as cancellation rather than being reported as a normal skipped frame.

Cancellation callbacks are invoked outside the coordinator lock so a future translation adapter cannot deadlock the coordinator by re-entering during cancellation.

## Unity lifecycle

`UnityLiveReadModeBehaviour`:

- subscribes to `ObservationPresented` while enabled;
- owns a lifetime cancellation token;
- exposes counters for processed, superseded, stale, and unconfigured observations;
- forwards only `Processed` aligned results into `UnityWorldTextTrackingBehaviour`;
- records the last exception rather than allowing an `async void` event callback failure to disappear silently;
- cancels active processing on disable and disposes the coordinator on destroy.

## Demo bootstrap

The generated demo scene wires the live runtime to the existing dictionary-backed `PhraseLayerDemoBehaviour` language pipeline. This proves the runtime composition without claiming general English→Japanese translation.

The dictionary engine returns source text for unknown entries, so arbitrary camera text still requires the reviewed offline NMT gate before PhraseLayer can be considered a complete Read Mode product.

## Remaining hardware/model gates

- offline English→Japanese NMT;
- reviewed Japanese-capable font asset;
- real Quest camera timestamp / pose / environment-depth synchronization;
- source-text masking/occlusion;
- device latency, memory, frame-time, thermal, and battery measurements.
