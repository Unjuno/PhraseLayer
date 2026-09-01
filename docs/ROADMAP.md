# Roadmap

Status notation: `[x]` implemented in repository/host validation, `[~]` active or partially implemented, `[ ]` not yet implemented. Hardware-dependent gates remain incomplete until Quest 3 measurements exist.

- [x] Gate 0: host Core build/tests, platform boundary, model manifest.
- [x] Gate 1: semantic segmentation, learner scores, adaptive assistance, bracket-free rendering, encounter freeze.
- [x] Gate 2: fake OCR and fake ASR vertical slices.
- [x] Gate 3: Unity 6 shell and editor fake demos.
- [~] Gate 4: Quest Passthrough Camera adapter and OCR benchmark. Pinned PP-OCR runtime/assets, real-camera anti-false-positive smoke harness, required camera-permission checks, and self-hosted Quest fixture workflow exist; real Quest 3 execution/latency measurements remain required.
- [~] Gate 5: OCR spans to stable physical placement/tracking/masking. Semantic↔OCR geometry, Passthrough viewport rays, MRUK live-depth environment raycast adapter, four-corner physical-plane fitting, temporal tracking, current-observation-only conservative source masking, Japanese world-text rendering, and end-to-end Quest smoke instrumentation exist. Real Quest registration error, stereo behavior, and mask visual-quality measurements remain required.
- [~] Gate 6: complete offline Read Mode and 30-minute performance test. The instrumented Android ARM64 IL2CPP fixture can exercise camera → OCR → adaptive planning → MRUK surface fit → mask → Japanese text with an explicit demo dictionary and records `product_translation_gate=false`. Real Quest fixture PASS, reviewed offline Marian English→Japanese product translation on the same path, hardware timestamp/pose/depth synchronization, and 30-minute performance/thermal/battery measurements are still required.
- [ ] Gate 7: microphone/VAD/local ASR and Listen Mode on the merged product branch.
- [~] Gate 8: persisted learner adaptation and comparison to fixed-density baseline. Persistence and deferred encounter adaptation are implemented; controlled baseline comparison remains required.
- [ ] Gate 9: privacy/license/VRC audit, Alpha/Beta, Store release.
