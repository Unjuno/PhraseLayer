# Benchmark protocol

Desktop results do not establish Quest 3 viability. Every real OCR, ASR, and translation adapter must record: headset/OS, Unity/Meta/runtime versions, model revision and quantization, backend/threads, cold start, p50/p95 steady latency, peak/steady PSS, XR frame time, CPU/GPU indicators, thermal behavior over 30 minutes, and battery change.

Measure stages separately:

```text
capture -> OCR -> segmentation/planning -> translation -> render
microphone -> VAD/ASR -> segmentation/planning -> translation -> subtitle
```

OCR fixtures should vary text size, distance, lighting, motion, perspective, and printed vs screen text. No device latency target is treated as verified before hardware measurement.
