# Local translation gate

PhraseLayer's official translation path is local-only. The current OPUS-MT candidate is a **revision-pinned source** candidate with a repeat-verified ONNX export, but it is still not a bundled runtime artifact. `models/models.lock.json` therefore remains `bundled=false` until every runtime, quality, Quest, and distribution gate below has evidence.

## Measured export result

The pinned `Helsinki-NLP/opus-mt-en-jap` revision `a863894cdd2b80f3bc1c5966734aee9ffec207d1` has now been exported twice through the metadata-only probe. The repeat verification commit is `8be5cc2ec258aec314cc9deb5a76485415e608b0`.

The correctness-first reference runtime set is:

- `encoder_model.onnx` — 171,553,398 bytes — SHA-256 `bb0d8d22053062bbd3695a468c88d1f84367eb195fa5f9fb75aa6c9548f57c59`;
- `decoder_model.onnx` — 291,878,261 bytes — SHA-256 `513bbf05f48da69847ce247e3245a5e84a814a7e591e8f544dea4854d202dc00`;
- combined reference size — 463,431,659 bytes / 441.963 MiB;
- ONNX IR version 8, opset 18;
- hidden size 512, vocabulary size 46,276;
- tokenizer encode/decode parity exact on the fixed fixtures;
- PyTorch generation vs ONNX Runtime token IDs and decoded text exact on the fixed fixtures.

This proves export reproducibility for those fixtures. It **does not prove translation quality**. The two fixed probe sentences produced weak Japanese, including semantic errors, so the model remains `quality_status=candidate-quality-review-required`. Export parity means the ONNX path reproduces the pinned upstream model; it does not mean the upstream model is good enough for PhraseLayer.

## Promotion sequence

1. **Pinned source** — `Helsinki-NLP/opus-mt-en-jap` at revision `a863894cdd2b80f3bc1c5966734aee9ffec207d1`, Apache-2.0 metadata, Marian architecture, and SentencePiece inputs are locked.
2. **ONNX export + token-exact parity** — PASS for the fixed probe fixtures. The probe exports the exact revision using `text2text-generation-with-past`, runs the pinned PyTorch reference and ONNX Runtime implementation, and requires identical token IDs and text.
3. **Content identity** — PASS for the measured export. Every produced file is size/SHA-256 **hash-pinned** in the probe JSON and the reference encoder/decoder identity is promoted into `models.lock.json`. Each ONNX graph records opsets, inputs/outputs, node/operator counts, and external-data locations.
4. **Metadata-only evidence** — PASS. GitHub uploads the JSON report and resolved Python toolchain only. Model-weight artifacts remain ephemeral and are not published by CI.
5. **Local Unity staging** — when the exact export files are available locally, `tools/prepare_unity_translation_assets.py --report <probe.json> --export-root <export-dir>` verifies every recorded size/SHA-256, requires exact parity, and atomically stages the measured files under `Assets/LocalTranslationAssets/OpusMtEnJap`. The entire `LocalTranslationAssets` directory is git-ignored. This staging step **does not prove Unity compatibility**.
6. **Real Unity import** — PENDING. Separately import staged ONNX artifacts with `com.unity.ai.inference@2.2.1`. PyTorch↔ORT parity and successful byte staging are not evidence of Unity compatibility.
7. **Unity runtime parity** — PENDING. Run fixed fixtures through the PhraseLayer `ITranslationTokenizer` + `IAutoregressiveTranslationBackend` implementation and compare with the reference token sequence.
8. **Translation quality gate** — PENDING and currently blocking adoption. Evaluate a representative semantic-unit corpus rather than the two export fixtures. At minimum track adequacy failures, named-entity corruption, negation/polarity errors, MWE handling, and Japanese readability. Do not treat token-exact ONNX parity as quality evidence.
9. **Quest execution** — PENDING. Measure cold load, per-segment latency, memory, thermal behavior, and output parity on Quest 3. The 441.963 MiB reference runtime is a measured storage-size fact, not evidence that it is practical on-device.
10. **Distribution review** — PENDING. Only reviewed, reproducible artifacts may be copied into release assets. The source candidate remains `bundled=false` until this gate is intentionally changed with artifact hashes and license review.

## Probe trigger policy

The conversion is intentionally not part of ordinary pushes. It runs through `workflow_dispatch`, or when the dedicated `ci/translation-export-probe.request` file is intentionally changed. That request file states the exact revision and the `metadata-only-no-weight-artifact` policy.

## Reference runtime boundary

Core owns the platform-neutral search policy through `AutoregressiveTranslationEngine` and pins the OPUS-MT forced-EOS rule through `OpusMtEnJapGenerationContract`. Unity provides the SentencePiece tokenizer and ONNX encoder/decoder backend. `CachingTranslationEngine` wraps that engine without coupling semantic assistance or learner state to Unity, ONNX Runtime, or a network service.

The reference backend deliberately uses `encoder_model.onnx` plus the non-cached `decoder_model.onnx`. It reruns the complete generated prefix at each step. That is expensive but keeps the first Unity correctness gate simpler than a KV-cache implementation. Runtime optimizations such as merged decoders, cached decoders, quantization, graph surgery, or a narrower beam remain experiments until they preserve the reference output and pass the Unity/Quest gates.
