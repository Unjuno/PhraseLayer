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
MoonshineGreedyAsrRuntime
        |
        +-- IAudioSeq2SeqGenerationBackend
        |       preprocess -> encode -> decoder/cache
        |
        +-- IAsrTokenDecoder
                token IDs -> transcript
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

Continuous microphone capture, buffering, VAD/utterance delimiting, and Unity/Android permissions remain platform concerns. `UnityMicrophoneUtteranceSourceBehaviour` currently provides a replaceable RMS/silence utterance segmenter and produces complete `AudioChunk` work items for Core.

## Repeatable audio fixtures

`WaveAudioDecoder` exists so ASR behavior can be tested before Unity or Quest microphone integration. It supports little-endian RIFF/WAVE PCM16 and IEEE float32 fixtures, downmixes multiple channels to mono, and preserves the caller timestamp. `AudioChunkPreprocessor` owns sample-rate conversion separately.

The pinned hosted reference fixture is Moonshine's Beckett clip from `moonshine-ai/moonshine-v2` revision:

```text
49bc3af5bb0d767d5156fb579fa5f9853b559bf3
```

The repository does not bundle the WAV. `fetch_moonshine_beckett_fixture.py` downloads that exact file only for validation and verifies its immutable identity before use.

## Live transcript behavior

`LiveListenModeCoordinator` uses the same latest-input-wins rule as live Read Mode:

- monotonically newer audio timestamps are accepted;
- equal/older work items are rejected as stale;
- a newer item cancels older in-flight ASR/translation work;
- a generation check rejects an older result even if an adapter ignores cancellation;
- cancellation callbacks are not invoked while the coordinator lock is held.

Partial ASR observations are returned to the caller but are not translated by default. This avoids repeatedly invoking offline translation for text that is still changing. A UI may opt into partial planning explicitly.

`ListenModeProcessingTimings` separates ASR time from adaptive language planning/translation time while retaining the Core total. `UnityLiveListenModeBehaviour` also records the outer Unity submission time, so real-device runs can identify whether Moonshine, Marian/language planning, or platform overhead is the dominant bottleneck.

## Pinned Moonshine Tiny source candidate

The source/model metadata candidate is `moonshine-ai/moonshine-tiny`, pinned to:

```text
390624ed33d594443aa4aa221f5b9f283b545b5a
```

Reviewed values represented by `MoonshineTinyAsrContract` include:

- English ASR
- raw waveform input
- 16,000 Hz input
- 32,768-token vocabulary
- hidden size 288
- 6 encoder + 6 decoder layers
- 8 attention heads
- decoder start/BOS = 1
- EOS/PAD = 2
- generation/position limit = 194
- decoder cache required

The exact `model.safetensors` identity is recorded without bundling the weight:

```text
size:   108,389,192 bytes
sha256: 867cd2215804859c55aa972d740bd5002be149b4e7526328c895d2408848c736
```

`validate_moonshine_snapshot_contract.py`, `validate_moonshine_evidence.py`, `validate_moonshine_lock_evidence.py`, and `validate_moonshine_local_source.py` keep the source snapshot, committed evidence, lock file, and any caller-supplied local weight aligned.

## Managed token decoder

The exact tokenizer pipeline was inspected from the pinned source revision. PhraseLayer generates a compact native-compatible token table and decodes token IDs through `MoonshineBinaryTokenDecoder` rather than requiring the Hugging Face/Rust tokenizer runtime inside Unity.

The managed decoder has been compared against pinned Hugging Face `tokenizers` behavior on the real tokenizer, including ordinary English, punctuation, accents/byte fallback, emoji, non-Latin text, whitespace, and special tokens. That tokenizer/decoder parity is a hosted correctness gate; it is not a Quest performance result.

## Published Moonshine v1 four-graph deployment reference

For execution work, PhraseLayer separately pins the published Moonshine v1 Tiny four-graph ONNX bundle at revision:

```text
35b4aae79f7d598a4d36d5252ec26ad642faab60
```

This deployment identity is deliberately separate from the Transformers source candidate above. Numerical equivalence between those two identities has not been asserted.

The reviewed deployment ABI is:

```text
preprocess
  float32 [B,S] -> float32 [B,T,288]

encode
  float32 [B,T,288] + int32 feature_length
  -> float32 [B,T,288]

uncached_decode
  int32 token + encoder state + int32 token_length
  -> logits [...,32768] + 24 cache states

cached_decode
  int32 token + encoder state + int32 token_length + 24 cache states
  -> logits [...,32768] + 24 updated cache states
```

The cached decoder's observed trailing cache dimensions are 8 heads x 36 = 288. Binding is positional because exporter-generated tensor names are not treated as a stable public ABI.

`UnityMoonshineV1GenerationBackend` keeps encoder/cache tensors in the inference backend and reads back logits for deterministic greedy selection. Hosted guarded compile verifies the reviewed Unity Inference Engine 2.2.x API surface, but real Unity import/execution remains a separate gate.

## Independent real-audio reference parity

`run_moonshine_v1_onnx_reference.py` executes the four pinned graphs independently with ONNX Runtime CPU. For the pinned Beckett WAV it currently emits 16 non-EOS tokens, reaches EOS on decoder step 17, and decodes to:

```text
Ever tried ever failed, no matter try again, fail again, fail better.
```

The committed evidence is `models/evidence/moonshine-v1-tiny.beckett.reference.json`. The fresh hosted run is compared against the committed token sequence and transcript by `validate_moonshine_v1_reference_trace.py`. The smallest observed winner-vs-runner-up logit margin in that reference is about 0.1956, so the fixture also exposes numerically fragile argmax changes instead of checking transcript text alone.

## Unity scene/runtime wiring

The branch contains:

- `UnityMoonshineAsrBootstrapBehaviour`: four `ModelAsset`s + token decoder -> `OfflineAsrEngine`;
- `UnityMicrophoneUtteranceSourceBehaviour`: Unity microphone ring capture + replaceable energy/silence utterance segmentation;
- `UnityLiveListenModeBehaviour`: microphone -> Moonshine -> adaptive language pipeline;
- `PhraseLayerLocalAsrAssets`: Editor validation/assignment for staged local ASR assets;
- `UnityMoonshineParityProbe`: Unity token-sequence comparison against the independent ONNX Runtime reference.

The self-hosted `Moonshine Unity Import` workflow stages only exact verified assets, imports them with Unity 6000.0.66f2, validates the positional ABI/token decoder, generates the independent reference trace, and requires exact Unity token parity when a compatible self-hosted runner is available.

## Quest measurement hooks

Successful Listen Mode utterances emit machine-readable lines beginning with:

```text
PHRASELAYER_LISTEN_METRIC
```

Current fields include audio duration, ASR milliseconds, adaptive-language planning/translation milliseconds, Core total, Unity submission total, processing/audio ratio, transcript length, and whether an adaptive plan was produced.

`summarize_listen_mode_metrics.py` computes median/p95/max phase timing and real-time ratios. `capture_quest_listen_mode_metrics.py` captures those lines through `adb logcat`, records Android/device provenance, and writes a measurement evidence bundle. It does not install or launch the app and does not claim that any capture is from Quest 3 unless the recorded device provenance says so.

## Proven now vs remaining gates

Hosted validation currently proves:

1. Core WAV/preprocessing, live-session, greedy ASR, token-decoder, graph-contract, and evidence behavior through automated tests.
2. Exact Moonshine source metadata/tokenizer/weight identity without bundling the weight.
3. Exact published v1 four-graph ONNX identity and positional ABI.
4. Managed token decoder parity against the pinned real tokenizer.
5. Independent ONNX Runtime CPU execution on a real speech fixture with a committed exact token trace and transcript.
6. Guarded compilation of the Unity Inference Engine backend/bootstrap against the reviewed API surface.

Still **not** proven:

1. Real Unity 6000.0.66f2 import/execution and exact Unity-vs-ONNX-Runtime token parity on the self-hosted runner.
2. Numerical equivalence between the published v1 deployment bundle and the separately pinned current Transformers source weight.
3. Android ARM64/IL2CPP build and runtime behavior.
4. Quest 3 microphone permission/capture behavior and utterance segmentation quality.
5. Quest 3 ASR/translation latency, native/GPU memory, thermal behavior, and recognition quality in realistic environments.
6. Whether the ~284 MB published v1 reference graph bundle is an acceptable final deployment footprint; optimization/quantization decisions require target-device measurements.

No Quest performance claim should be inferred from hosted CPU inference, model size, or guarded compilation.
