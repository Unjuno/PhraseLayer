# Gate 4 OCR candidate: PP-OCRv6 tiny ONNX

This document records the first OCR candidate selected for Quest-side evaluation. It is a **candidate**, not a declaration that the models import or run correctly in Unity AI Inference.

## Candidate pair

| Role | Upstream | Pinned revision | Artifact |
|---|---|---|---|
| detector | `PaddlePaddle/PP-OCRv6_tiny_det_onnx` | `2ba1506c0380b8f0b03dd142459aac66d4421f6c` | `inference.onnx` |
| recognizer | `PaddlePaddle/PP-OCRv6_tiny_rec_onnx` | `2612ab37152ae0a677521bae4e1e3d4fb4cf7c30` | `inference.onnx` |

Both upstream repositories identify the model as Apache-2.0. No model weight is bundled in PhraseLayer at this stage; redistribution review remains a separate gate.

The detector artifact has an upstream Git-LFS object record of:

- size: `1,780,590` bytes
- SHA-256: `193bab7a04fca699a6c82e6abb5b81bdb28177f0abd4062552b04908dafb19f8`

The recognizer artifact hash/byte count is deliberately left unresolved in `models.lock.json` until it can be verified directly rather than inferred from a converted copy.

## Runtime target

- Unity: `6000.0.66f2`
- Unity AI Inference: `com.unity.ai.inference@2.2.1`
- Meta MRUK baseline: `85.0.0`
- target device: Meta Quest 3

The ONNX graphs have **not** yet been imported into Unity AI Inference 2.2.1 in a real Unity Editor. Operator support, tensor layout, texture preprocessing, output decoding, precision, and quantization therefore remain unverified.

## Detector preprocessing evidence

The upstream PP-OCRv6 tiny detector inference configuration specifies:

- BGR decoded image
- normalization scale `1/255`
- mean `[0.485, 0.456, 0.406]`
- standard deviation `[0.229, 0.224, 0.225]`
- HWC normalization followed by CHW conversion
- DB post-process threshold `0.2`
- box threshold `0.4`
- unclip ratio `1.4`
- maximum candidates `3000`

PhraseLayer should reproduce this preprocessing/post-processing only after the ONNX graph input/output contract is inspected. `OcrLetterboxTransform` already owns the source/model geometry mapping; the production preprocessor must use the exact same resize/padding geometry that it reports to that transform.

## Recognition evidence

The upstream tiny recognizer is described as the smallest PP-OCRv6 recognition tier, roughly 1.1M parameters, with multilingual recognition support. That makes it a plausible Quest candidate, but upstream server/GPU benchmark numbers are not evidence of Quest latency.

## Required checks before weights enter the app

1. Download each artifact at the pinned revision and verify its hash/size locally.
2. Inspect ONNX input/output names, shapes, dtypes and operator set.
3. Import both graphs into Unity AI Inference 2.2.1 without graph conversion errors.
4. Confirm the camera texture orientation/channel order against the detector preprocessing contract.
5. Implement detector DB post-processing and recognizer decoding against observed tensor outputs, not guessed APIs.
6. Measure Quest 3 cold start, p50/p95 OCR latency, peak/steady PSS and XR frame time.
7. Test text size, distance, lighting and head motion before closing Gate 4.

## Source references

- https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_det_onnx
- https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_rec_onnx
- https://huggingface.co/PaddlePaddle/PP-OCRv6_tiny_det/blob/main/inference.yml
- https://arxiv.org/abs/2606.13108
