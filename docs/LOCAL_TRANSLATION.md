# Local translation gate

PhraseLayer's official translation path is local-only. The current OPUS-MT candidate is a **revision-pinned source** candidate, not a bundled runtime artifact. `models/models.lock.json` therefore remains `bundled=false` until every gate below has evidence.

## Promotion sequence

1. **Pinned source** — `Helsinki-NLP/opus-mt-en-jap` at revision `a863894cdd2b80f3bc1c5966734aee9ffec207d1`, Apache-2.0 metadata, Marian architecture, and SentencePiece inputs are locked.
2. **ONNX export** — run the manual `Translation ONNX export probe` workflow. The exporter passes the exact revision to Optimum, disables remote code, asks for split encoder/decoder text2text generation, and discovers the files that were actually produced rather than guessing filenames.
3. **Content identity** — every produced file is size/SHA-256 **hash-pinned** in `translation-export.manifest.json`. Each ONNX graph records opsets, input/output tensor metadata, node/operator counts, and external-data locations.
4. **Real Unity import** — import the measured ONNX artifacts with `com.unity.ai.inference@2.2.1`. A Python/ONNX success is not evidence of Unity compatibility.
5. **Runtime parity** — run fixed English→Japanese fixtures through the Unity implementation and compare tokenization/generation behavior against a reference Marian implementation.
6. **Quest execution** — measure cold load, per-segment latency, memory, thermal behavior, and output parity on Quest 3. Only then can runtime compatibility be promoted.
7. **Distribution review** — only reviewed, reproducible artifacts may be copied into release assets. The source candidate remains `bundled=false` until this gate is intentionally changed with its hashes and license review.

## Why the export is manual

The export downloads a large model and runs PyTorch/ONNX conversion. It therefore does not run on ordinary pushes. `workflow_dispatch` makes the compute cost explicit and keeps normal Core/Unity CI fast.

## Reference runtime boundary

The Core API remains `ITranslationEngine`. `CachingTranslationEngine` may wrap the eventual local Marian/ONNX implementation, so the learning and assistance pipelines do not depend on ONNX, Unity, or a network service.

The initial export deliberately uses the correctness-oriented `text2text-generation` path without past-key-value optimization. Decoder caching, quantization, graph surgery, and beam-search optimization are later experiments and must preserve the reference output before they are adopted.
