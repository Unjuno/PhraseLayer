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

# Intentionally no -nographics: TextureConverter and GPUCompute must execute on a real graphics device.
"$UNITY_EDITOR" \
  -batchmode \
  -projectPath "$PROJECT" \
  -executeMethod PhraseLayer.Unity.Editor.PhraseLayerPaddleOcrGpuPreprocessProbe.RunBatch \
  -logFile -

printf 'PASS: real Unity PP-OCR GPU texture -> tensor -> normalization parity probe\n'
