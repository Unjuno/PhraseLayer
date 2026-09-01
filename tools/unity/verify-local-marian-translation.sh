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
  "$PROJECT/Assets/LocalTranslationAssets/Marian/encoder_model.onnx" \
  "$PROJECT/Assets/LocalTranslationAssets/Marian/decoder_model.onnx" \
  "$PROJECT/Assets/LocalTranslationAssets/Marian/decoder_with_past_model.onnx" \
  "$PROJECT/Assets/LocalTranslationAssets/PhraseLayerMarianAssets.manifest.json" \
  "$PROJECT/Assets/Resources/LocalTranslationAssets/source.spm.bytes" \
  "$PROJECT/Assets/Resources/LocalTranslationAssets/target.spm.bytes" \
  "$PROJECT/Assets/Resources/LocalTranslationAssets/vocab.json" \
  "$PROJECT/Assets/Resources/LocalTranslationAssets/marian-reference.json" \
  "$PROJECT/Assets/LocalTokenizerRuntime/PhraseLayer.Tokenization.Microsoft.dll" \
  "$PROJECT/Assets/LocalTokenizerRuntime/Microsoft.ML.Tokenizers.dll"; do
  if [[ ! -s "$file" ]]; then
    echo "Required local Marian Unity asset is missing or empty: $file" >&2
    exit 2
  fi
done

# Intentionally no -nographics: both Marian backends use GPUCompute. The probe compares exact source token IDs,
# generated token IDs, and decoded text against an offline Transformers fp32 CPU reference for both backend variants.
"$UNITY_EDITOR" \
  -batchmode \
  -projectPath "$PROJECT" \
  -executeMethod PhraseLayer.Unity.Editor.PhraseLayerLocalMarianAssets.RunTranslationParityProbeBatch \
  -logFile -

printf 'PASS: real Unity Marian exact-token translation parity for baseline and device-resident backends\n'
