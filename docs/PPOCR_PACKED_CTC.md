# Packed recognizer CTC boundary

This document refines the recognizer-output boundary in OCR_RUNTIME.md. It is an implementation/test contract, not a claim that real Unity, Android, or Quest execution has passed.

## Runtime ABI

The reviewed package remains com.unity.ai.inference@2.2.1 and the Editor remains Unity 6000.0.66f2. Production requests GPUCompute. The GPU-reduction capability flag is false for a CPU backend; selecting CPU must not create GPU evidence. GPUCompute selection itself does not prove that every internal operator avoided a CPU fallback.

The recognizer exposes a float probability tensor with shape [1,time,class]. PackReviewedCtcOutput applies ArgMax and ReduceMax on the last axis, casts indices to float32, inserts a trailing dimension, and concatenates index followed by score. The result is float32 [1,time,2], interleaved by timestep. It is not [1,2,time].

| Dimension/value | Meaning | Unit | Definition | Domain | Type |
| --- | --- | --- | --- | --- | --- |
| batch | Batch size | 1 (dimensionless) | First dimension | Exactly 1 | Integer scalar |
| time | CTC timestep count | 1 | Second dimension of both tensors | Positive Int32; checked buffer length | Integer scalar |
| class | Classes including blank | 1 | Last probability dimension | 1 through 16,777,216; pinned model expects dictionary count plus one | Integer scalar |
| class index | Winning label | 1 | First value of each packed pair | Exact integer, non-negative and less than class count | float32 on GPU; Int32 after validation |
| score | Winning probability value | 1 | Second value of each pair | Finite, from 0 through 1 | float32 scalar |

The conservative class-count cap makes every permitted class index exactly representable in float32. The pinned dictionary contains 6905 tokens and adds one CTC blank. It is well inside the cap. This packing contract must not be reused with float16 indices: its exact-integer domain is smaller. ARGBHalf in the image preprocessing stage is unrelated to the float32 output packing buffer.

PaddleCtcPackedOutput validates both shapes before download in Unity and again when unpacking on CPU. It rejects invalid ranks, batches, timestep/pair dimensions, truncated/trailing buffers, size overflow, NaN/infinite/fractional/out-of-range IDs, and non-finite or out-of-range winning scores. Blank and duplicate timesteps are checked before CTC filtering. Dictionary compatibility is then checked by ValidateRecognizerReduced, including when callers invoke the convenience Decode method directly.

Dimension check: both tensors contain dimensionless values. Packing transfers two float32 values, or eight payload bytes, per timestep, before API/transport overhead. This byte count is not a latency or memory-usage measurement.

## Readback and worker ownership

ExecuteReduced explicitly calls ReadbackAndClone once, on the packed tensor. DownloadToArray operates on its CPU clone. The full probability output is inspected only for shape. Core splits the downloaded pairs into class indices and winning scores, then the existing CTC decoder removes consecutive duplicates and blanks.

Only the reduced-output Worker is retained. Execute remains a host oracle that creates and disposes a temporary full-output Worker. No production call has been switched back to the full-matrix path.

The single explicit readback contract does not establish the number of driver waits, internal backend synchronization operations, or allocation sizes. Those require runtime measurement. Detector probability-map readback and CPU DB post-processing also remain in place: OCR is not entirely GPU-resident.

## Executable host tests

PaddleCtcPackedOutputTests runs the actual production unpacking code without Unity. Cases cover interleaving, blank-separated repeats, confidence preservation, source-buffer independence, wrong layouts with identical element counts, invalid IDs/scores including blank/duplicate values, float32 bounds, checked overflow, nulls, and dictionary mismatch.

test_ppocr_packed_gate.py tests the static guard itself with mutations. Adding a second readback, changing the readback source, or commenting out the shape check must fail. A readback mentioned only in a comment does not count. The shell flag check also rejects quoted/same-line -nographics arguments without rejecting explanatory comments.

Static checks are supplemental wiring checks, not a C# semantic proof or GPU execution evidence.

## Real Unity probes, no headset

PhraseLayerPaddleOcrRecognizerGpuReductionProbe.RunBatch includes two model-independent GPU packing cases and three real-model image fixtures.

The first injected tensor covers first-index ties, all-equal blank ties, repeated labels and blank-separated repeated labels. Its decoded result must be bbd with three emitted tokens and matching confidence. The second tensor uses the full pinned class count and tests the highest class index and a tie with the lowest nonblank index. Both pass actual runtime inputs to the graph rather than Functional.Constant so constant folding cannot replace the test with precomputed outputs. The GPU operators are the production PackReviewedCtcOutput helper.

The real-model cases use widths 64, 192 and 321 at height 48. Every timestep must have the same class index as the full-matrix CPU oracle; maximum score and decoded-confidence absolute errors must not exceed 0.000001. Decoded text and emitted-token counts must match exactly. The full oracle checks finite values in [0,1] and per-row sums within 0.001 of one. Winning-score range checks alone do not prove full probability normalization.

RunSyntheticBatch is also available as a real-Unity entry point requiring no model download. Neither entry point uses a camera or ADB.

The shared verify-local-ocr-inference.sh chains verify-recognizer-gpu-reduction.sh before reporting overall OCR success. The reduction shell uses a fresh temporary log, propagates a Unity failure through pipefail, and requires the full successful completion marker including readbacks_per_crop=1. Exit code zero without the marker fails. The temporary synthetic host log is removed on exit and is not an uploaded real-world OCR diagnostic.

## Claim boundary

Hosted CI can establish Core test results, compile-shell compatibility with reviewed declarations, and static integration. Only executing the probe in real Unity establishes numerical parity on that host. Neither implies Android/Quest runtime parity or measured frame time, thermal behavior, camera pixel/pose alignment, or stereo registration. camera_pixel_pose_sync_verified must remain false until its separate target-device evidence exists.
