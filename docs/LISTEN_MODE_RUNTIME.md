# Listen Mode runtime

PhraseLayer Listen Mode reuses the same semantic segmentation, learner understanding, assistance planning, and translation pipeline as Read Mode. The input boundary changes from OCR text to ASR text; adaptive replacement semantics do not.

## Core pipeline

```text
microphone / WAV fixture
        |
        v
AudioChunk (mono float samples + sample rate + timestamp)
        |
        v
OfflineAsrEngine
  - validates/clamps finite samples
  - deterministic sample-rate conversion
        |
        v
IOfflineAsrRuntime
        |
        v
AsrObservation(text, isFinal)
        |
        v
ListenModeObservationProcessor
        |
        +-- partial: expose transcript only by default
        |
        +-- final: LanguagePipeline
                    |
                    v
              semantic units
                    |
                    v
              learner model
                    |
                    v
            assistance selection
                    |
                    v
              translation engine
                    |
                    v
              mixed-language text
```

Continuous microphone capture, buffering, VAD/utterance delimiting, and Unity/Android permissions are intentionally outside `PhraseLayer.Core`. A platform adapter produces complete recognition work items (`AudioChunk`) and submits them to the Core boundary.

## Repeatable audio fixtures

`WaveAudioDecoder` exists so ASR behavior can be tested before Unity or Quest microphone integration. It supports little-endian RIFF/WAVE PCM16 and IEEE float32 fixtures, downmixes multiple channels to mono, and preserves the caller timestamp. `AudioChunkPreprocessor` owns sample-rate conversion separately.

This is not intended as a general media decoder. Unsupported codecs/containers should be converted to a reviewed WAV fixture by tooling outside Core.

## Live transcript behavior

`LiveListenModeCoordinator` uses the same latest-input-wins rule as live Read Mode:

- monotonically newer audio timestamps are accepted;
- equal/older work items are rejected as stale;
- a newer item cancels older in-flight ASR/translation work;
- a generation check rejects an older result even if an adapter ignores cancellation;
- cancellation callbacks are not invoked while the coordinator lock is held.

Partial ASR observations are returned to the caller but are not translated by default. This avoids repeatedly invoking offline translation for text that is still changing. A UI may opt into partial planning explicitly.

## Initial offline ASR candidate: Moonshine Tiny (English)

The first concrete candidate is `moonshine-ai/moonshine-tiny`, pinned in Core to revision:

```text
390624ed33d594443aa4aa221f5b9f283b545b5a
```

Reviewed compatibility values are represented by `MoonshineTinyAsrContract`:

- English ASR
- `MoonshineForConditionalGeneration`
- raw waveform feature size 1
- 16,000 Hz input
- input normalization disabled by the model preprocessor
- 32,768-token vocabulary
- hidden size 288
- 6 encoder + 6 decoder layers
- 8 attention heads
- decoder start/BOS = 1
- EOS/PAD = 2
- generation/position limit = 194
- decoder cache required

`validate_moonshine_snapshot_contract.py` checks those values against a caller-staged immutable local snapshot and emits hashes for the small metadata/tokenizer artifacts. It does not download or commit model weights.

## Core generation runtime

`MoonshineGreedyAsrRuntime` is the correctness-first generation loop. It owns no inference framework. A concrete `IAudioSeq2SeqGenerationBackend` owns encoder execution and decoder KV-cache tensors, while an `IAsrTokenDecoder` converts generated token IDs to text.

The baseline is intentionally greedy. It stops at EOS and rejects vocabulary/non-finite-logit drift. Streaming hypotheses, sampling, and any alternative decoding policy must be implemented as separately tested runtime variants rather than silently approximated in this baseline.

## What is not proven yet

Hosted Core tests do **not** prove the following:

1. Moonshine model weights or an ONNX export have been validated for PhraseLayer.
2. Moonshine tokenizer output has been compared numerically/textually with the chosen runtime backend.
3. Unity AI Inference can import and execute the chosen Moonshine graphs.
4. Android ARM64/IL2CPP can execute the runtime.
5. Quest 3 microphone capture is correctly permissioned, buffered, and timestamped.
6. Quest 3 latency, memory, thermals, or recognition quality are acceptable.

Those remain explicit later gates. No Quest performance claim should be inferred from the model size or hosted CI.
