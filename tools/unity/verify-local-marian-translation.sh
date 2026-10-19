#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/unity/PhraseLayer.Unity"
UNITY_EDITOR="${UNITY_EDITOR:-}"
EVIDENCE="${PHRASELAYER_MARIAN_PARITY_EVIDENCE_PATH:-$PROJECT/Temp/PhraseLayer.marian-unity-parity-evidence.json}"

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
  "$PROJECT/Assets/LocalTokenizerRuntime/Microsoft.ML.Tokenizers.dll" \
  "$PROJECT/Assets/LocalTokenizerRuntime/Google.Protobuf.dll" \
  "$PROJECT/Assets/LocalTokenizerRuntime/link.xml"; do
  if [[ ! -s "$file" ]]; then
    echo "Required local Marian Unity asset is missing or empty: $file" >&2
    exit 2
  fi
done

mkdir -p "$(dirname "$EVIDENCE")"
rm -f "$EVIDENCE"
export PHRASELAYER_MARIAN_PARITY_EVIDENCE_PATH="$EVIDENCE"

# Intentionally no -nographics: both Marian backends use GPUCompute. The probe compares exact source token IDs,
# generated token IDs, decoded text, and semantic-span LanguagePipeline output against the offline Transformers
# fp32 CPU reference. Evidence is written by the Unity process itself only after every assertion succeeds.
"$UNITY_EDITOR" \
  -batchmode \
  -projectPath "$PROJECT" \
  -executeMethod PhraseLayer.Unity.Editor.PhraseLayerMarianParityEvidence.RunBatch \
  -logFile -

test -s "$EVIDENCE" || { echo "Real Unity Marian parity evidence was not produced: $EVIDENCE" >&2; exit 3; }
printf 'PASS: real Unity Marian exact-token translation parity for baseline and device-resident backends; evidence=%s\n' "$EVIDENCE"
