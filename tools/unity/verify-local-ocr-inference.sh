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
  "$PROJECT/Assets/LocalOcrAssets/PaddleOCR/detector.onnx" \
  "$PROJECT/Assets/LocalOcrAssets/PaddleOCR/recognizer.onnx" \
  "$PROJECT/Assets/LocalOcrAssets/PaddleOCR/ppocr_keys.txt" \
  "$PROJECT/Assets/LocalOcrAssets/PaddleOCR/ppocr_keys.manifest.json"; do
  if [[ ! -s "$file" ]]; then
    echo "Required staged PP-OCR asset is missing or empty: $file" >&2
    echo "Run python tools/prepare_unity_ocr_assets.py first." >&2
    exit 2
  fi
done

# Intentionally no -nographics: detector TextureConverter, detector/recognizer GPUCompute workers and their
# output readbacks require an actual graphics device. This is a host-side model/runtime gate, not a Quest gate.
"$UNITY_EDITOR" \
  -batchmode \
  -projectPath "$PROJECT" \
  -executeMethod PhraseLayer.Unity.Editor.PhraseLayerLocalOcrAssets.RunLocalInferenceProbeBatch \
  -logFile -

# Production recognition does not download the full [time,class] probability matrix. Before any host/device build may
# rely on that optimization, require a separate real-Unity process to prove GPU ArgMax/ReduceMax gives exactly the
# same greedy CTC result as the retained full-matrix oracle for the pinned recognizer.
bash "$ROOT/tools/unity/verify-recognizer-gpu-reduction.sh"

printf 'PASS: real Unity pinned PP-OCR detector+recognizer synthetic GPU inference plus recognizer GPU CTC reduction parity\n'
