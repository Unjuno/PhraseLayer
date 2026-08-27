# Roadmap

Status notation: `[x]` implemented in repository/host validation, `[~]` active or partially implemented, `[ ]` not yet implemented. Hardware-dependent gates remain incomplete until Quest 3 measurements exist.

- [x] Gate 0: host Core build/tests, platform boundary, model manifest.
- [x] Gate 1: semantic segmentation, learner scores, adaptive assistance, bracket-free rendering, encounter freeze.
- [x] Gate 2: fake OCR and fake ASR vertical slices.
- [x] Gate 3: Unity 6 shell and editor fake demos.
- [~] Gate 4: Quest Passthrough Camera adapter and OCR benchmark. Adapter, PP-OCR runtime, asset staging, and demo-scene wiring exist; real Quest 3 execution/benchmark remains required.
- [~] Gate 5: OCR spans to stable world placement/tracking. Image/viewport geometry and semantic-region alignment exist; stable physical-text replacement rendering/tracking remains required.
- [ ] Gate 6: complete offline Read Mode and 30-minute performance test. Offline English→Japanese NMT plus Quest endurance measurements are still required.
- [ ] Gate 7: microphone/VAD/local ASR and Listen Mode.
- [~] Gate 8: persisted learner adaptation and comparison to fixed-density baseline. Persistence and deferred encounter adaptation are implemented; controlled baseline comparison remains required.
- [ ] Gate 9: privacy/license/VRC audit, Alpha/Beta, Store release.
