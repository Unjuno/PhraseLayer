#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/unity/PhraseLayer.Unity"
UNITY_EDITOR="${UNITY_EDITOR:-}"
if [[ -z "$UNITY_EDITOR" ]]; then
  echo "UNITY_EDITOR must point to the Unity 6000.0.66f2 Editor executable." >&2
  exit 2
fi
if [[ ! -x "$UNITY_EDITOR" ]]; then
  echo "UNITY_EDITOR is not executable." >&2
  exit 2
fi
for file in \
  "$PROJECT/Assets/LocalOcrAssets/PaddleOCR/recognizer.onnx" \
  "$PROJECT/Assets/LocalOcrAssets/PaddleOCR/ppocr_keys.txt"; do
  if [[ ! -s "$file" ]]; then
    echo "Required staged recognizer reduction parity asset is missing or empty." >&2
    exit 2
  fi
done

# Intentionally no -nographics: full-model oracle and packed ArgMax/ReduceMax run with GPUCompute requested.
# Tee a fresh host-only synthetic log so an exit code without probe completion cannot masquerade as a PASS.
PARITY_LOG="$(mktemp "${TMPDIR:-/tmp}/phraselayer-packed-parity.XXXXXX")"
trap 'rm -f "$PARITY_LOG"' EXIT
"$UNITY_EDITOR" \
  -batchmode \
  -projectPath "$PROJECT" \
  -executeMethod PhraseLayer.Unity.Editor.PhraseLayerPaddleOcrRecognizerGpuReductionProbe.RunBatch \
  -logFile - | tee "$PARITY_LOG"
grep -Fq 'PhraseLayer PP-OCR recognizer GPU reduction parity PASS; model_fixtures=3 synthetic_fixtures=2 packed_layout=BTC2 class_indices=exact scores_tolerance=0.000001 readbacks_per_crop=1 decoded_text=exact emitted_tokens=exact confidence=parity quest_executed=false' "$PARITY_LOG"
printf 'PASS: real Unity PP-OCR recognizer full-matrix versus GPU ArgMax/ReduceMax CTC reduction parity; packed output contract has one explicit readback per crop\n'
