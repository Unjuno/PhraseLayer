# October 2026 lightweight-model review — Quest 3

## Decision

**Keep PP-OCRv6 tiny detector and recognizer.** The pinned ONNX files in
models/models.lock.json total exactly 6,243,229 bytes (1,780,590 detection
+ 4,462,639 recognition). Both are already the edge-tier 2026 models.
Do not swap to PP-OCRv5_mobile just because "mobile" is in its name.
The published PP-OCRv6 report lists printed-English detection Hmean
92.3% versus PP-OCRv5_mobile 91.0%, and printed-English recognition
88.4% versus 86.0% on its own evaluation data. These are *not* Quest
performance or PhraseLayer real-world accuracy measurements.

**Evaluate, but do not automatically deploy, Marian INT8.** The pinned
Helsinki-NLP/opus-mt-en-jap source PyTorch file is 273,663,309 bytes.
CTranslate2 4.8.3 supports MarianMT and AArch64 CPU with int8_float32.
That makes it a relevant alternative to the existing Unity 2.2.1
FP32/ONNX path; it is NOT a drop-in Unity ModelAsset replacement.
A native Android ARM64 library, JNI adapter, tokenizer parity, device
threading, privacy, continuous use, and a license/redistribution review
are required before it can become the shipping backend.

A 2026-era 3.3B Japanese translation LLM (e.g. CAT-Translate-3.3b)
is not preferable for a small, continuously-running Quest 3 overlay:
it is orders of magnitude larger than the pinned 6-layer Marian.
A direct English→Japanese tiny distilled model with verified compatible
tokenizer and acceptable quality has not been established by this review.
Never pivot en→other language→ja to fill the gap.

## Reproducible INT8 experiment (opt in)

From a machine that already holds an exact locked local Marian
source snapshot (including pytorch_model.bin):

~~~
python -m pip install -r tools/requirements-marian-export.txt
python -m pip install -r tools/requirements-marian-ct2-audit.txt
python tools/benchmark_marian_ct2_int8.py \
  --snapshot /private/local/locked/opus-mt-en-jap \
  --report /private/local/ct2-comparison.json
~~~

The runner performs **no download** and verifies all 8 source files
against models.lock.json size and SHA-256 *before* importing conversion
packages. It converts two disposable candidates from precisely the same
weights: CT2 float32 (control) and CT2 int8_float32 (experiment).
The 24 deterministic semantic-span phrases use beam=1 and output limit
64; the same tokenization and decode path runs on both. It records
model directory sizes, exact decoded agreement, critical-sign agreement
and host CPU p50/p95 only. The full model data and translations are
never written into the report. Temporary conversions are deleted at
the end, including on failure. A nonzero exit code is intentional when
the INT8 candidate fails the trial gate.

The **trial** thresholds are <=65% converted-size ratio, >=95% full
exact agreement, 100% agreement plus Japanese-script sanity for 12
critical sign spans, and no empty outputs. These are conservative
engineering thresholds rather than BLEU/COMET quality guarantees.
Even a trial PASS MUST NOT silently switch the runtime. Compare against
human-reviewed Japanese references, then run native ARM64/Quest hardware
integration first. Host timing is never a Quest performance claim.
The existing Marian exact-token Unity evidence remains the baseline.

Run dependency-free tests without the source weights or CTranslate2:

~~~
python tools/test_benchmark_marian_ct2_int8.py
~~~

## Other practical reductions

Before changing the OCR encoder or adding an LLM, profile Quest latency
per detector, crop, recognizer, and translator. The existing OCR recognizer
already uses GPU CTC argmax and one packed readback per crop. A smaller
weight file alone does not guarantee lower XR frame time: decoder cache,
shader scheduling, runtime initialization, thread contention and
allocations are relevant.

Do not reinterpret upstream server benchmarks as Quest measurements.
Do not add weights/quantized models to Git, workflow artifacts or a
redistributable APK while review is pending.

## Primary evidence

- PaddleOCR PP-OCRv6 official metrics:
  https://github.com/PaddlePaddle/PaddleOCR/blob/main/docs/version3.x/algorithm/PP-OCRv6/PP-OCRv6.en.md
- Official PP-OCRv6 tiny ONNX models:
  https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_det_onnx
  https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_rec_onnx
- CTranslate2 ARM64 and quantization support:
  https://opennmt.net/CTranslate2/hardware_support.html
  https://opennmt.net/CTranslate2/quantization.html
  https://opennmt.net/CTranslate2/guides/transformers.html
- Baseline Marian model:
  https://huggingface.co/Helsinki-NLP/opus-mt-en-jap
