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
  echo "UNITY_EDITOR is not executable: $UNITY_EDITOR" >&2
  exit 2
fi

for file in \
  "$PROJECT/Assets/LocalOcrAssets/PaddleOCR/recognizer.onnx" \
  "$PROJECT/Assets/LocalOcrAssets/PaddleOCR/ppocr_keys.txt"; do
  if [[ ! -s "$file" ]]; then
    echo "Required staged recognizer reduction parity asset is missing or empty: $file" >&2
    exit 2
  fi
done

# Intentionally no -nographics: both the full recognizer oracle and GPU ArgMax/ReduceMax path execute on GPUCompute.
"$UNITY_EDITOR" \
  -batchmode \
  -projectPath "$PROJECT" \
  -executeMethod PhraseLayer.Unity.Editor.PhraseLayerPaddleOcrRecognizerGpuReductionProbe.RunBatch \
  -logFile -

printf 'PASS: real Unity PP-OCR recognizer full-matrix versus GPU ArgMax/ReduceMax CTC reduction parity\n'
