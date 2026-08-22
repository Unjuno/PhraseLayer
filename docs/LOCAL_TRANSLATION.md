# Local translation gate

PhraseLayer's official translation path is local-only. The current OPUS-MT candidate is a **revision-pinned source** candidate, not a bundled runtime artifact. `models/models.lock.json` therefore remains `bundled=false` until every gate below has evidence.

## Promotion sequence

1. **Pinned source** — `Helsinki-NLP/opus-mt-en-jap` at revision `a863894cdd2b80f3bc1c5966734aee9ffec207d1`, Apache-2.0 metadata, Marian architecture, and SentencePiece inputs are locked.
2. **ONNX export + token-exact parity** — the probe exports the exact revision using `text2text-generation-with-past`, runs the pinned PyTorch reference and ONNX Runtime implementation on fixed English→Japanese fixtures, and requires identical token IDs and text.
3. **Content identity** — every produced file is size/SHA-256 **hash-pinned** in the probe JSON. Each ONNX graph records opsets, inputs/outputs, node/operator counts, and external-data locations.
4. **Metadata-only evidence** — GitHub uploads the JSON report and resolved Python toolchain only. Model-weight artifacts remain ephemeral and are not published by CI.
5. **Local Unity staging** — when the exact export files are available locally, `tools/prepare_unity_translation_assets.py --report <probe.json> --export-root <export-dir>` verifies every recorded size/SHA-256, requires exact parity, and atomically stages the measured files under `Assets/LocalTranslationAssets/OpusMtEnJap`. The entire `LocalTranslationAssets` directory is git-ignored. This staging step **does not prove Unity compatibility**.
6. **Real Unity import** — separately import staged ONNX artifacts with `com.unity.ai.inference@2.2.1`. PyTorch↔ORT parity and successful byte staging are not evidence of Unity compatibility.
7. **Unity runtime parity** — run fixed fixtures through the PhraseLayer `ITranslationTokenizer` + `IAutoregressiveTranslationBackend` implementation and compare with the reference token sequence.
8. **Quest execution** — measure cold load, per-segment latency, memory, thermal behavior, and output parity on Quest 3. Only then can runtime compatibility be promoted.
9. **Distribution review** — only reviewed, reproducible artifacts may be copied into release assets. The source candidate remains `bundled=false` until this gate is intentionally changed with artifact hashes and license review.

## Probe trigger policy

The conversion is intentionally not part of ordinary pushes. It runs through `workflow_dispatch`, or when the dedicated `ci/translation-export-probe.request` file is intentionally changed. That request file states the exact revision and the `metadata-only-no-weight-artifact` policy.

## Reference runtime boundary

Core already owns the platform-neutral search policy through `AutoregressiveTranslationEngine` and pins the OPUS-MT forced-EOS rule through `OpusMtEnJapGenerationContract`. Unity will provide the SentencePiece tokenizer and ONNX encoder/decoder backend. `CachingTranslationEngine` wraps that engine without coupling semantic assistance or learner state to Unity, ONNX Runtime, or a network service.

The current probe uses a cached-decoder export for realistic size/parity measurement. Runtime optimizations such as merged decoders, quantization, graph surgery, or a narrower beam remain experiments until they preserve the reference output and pass the Unity/Quest gates.
