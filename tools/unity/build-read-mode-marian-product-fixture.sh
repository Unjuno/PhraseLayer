#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT_PATH="${PHRASELAYER_UNITY_PROJECT_PATH:-$ROOT/unity/PhraseLayer.Unity}"

: "${UNITY_EDITOR:?UNITY_EDITOR must point to the Unity 6000.0.66f2 Editor executable.}"
: "${PHRASELAYER_JAPANESE_FONT_SOURCE:?PHRASELAYER_JAPANESE_FONT_SOURCE must point to a locally reviewed Japanese-capable font.}"
: "${PHRASELAYER_READ_MODE_MARIAN_PRODUCT_APK_PATH:?PHRASELAYER_READ_MODE_MARIAN_PRODUCT_APK_PATH must name a local APK output path.}"

[[ -x "$UNITY_EDITOR" ]] || { echo "UNITY_EDITOR is not executable: $UNITY_EDITOR" >&2; exit 2; }
[[ -d "$PROJECT_PATH/Assets" ]] || { echo "Unity project Assets directory is missing: $PROJECT_PATH/Assets" >&2; exit 2; }

for required in \
  "$PROJECT_PATH/Assets/LocalOcrAssets/PaddleOCR/detection.onnx" \
  "$PROJECT_PATH/Assets/LocalOcrAssets/PaddleOCR/recognition.onnx" \
  "$PROJECT_PATH/Assets/LocalOcrAssets/PaddleOCR/PhraseLayerOcrAssets.manifest.json" \
  "$PROJECT_PATH/Assets/LocalTranslationAssets/Marian/encoder_model.onnx" \
  "$PROJECT_PATH/Assets/LocalTranslationAssets/Marian/decoder_model.onnx" \
  "$PROJECT_PATH/Assets/LocalTranslationAssets/Marian/decoder_with_past_model.onnx" \
  "$PROJECT_PATH/Assets/LocalTranslationAssets/PhraseLayerMarianAssets.manifest.json" \
  "$PROJECT_PATH/Assets/Resources/LocalTranslationAssets/source.spm.bytes" \
  "$PROJECT_PATH/Assets/Resources/LocalTranslationAssets/target.spm.bytes" \
  "$PROJECT_PATH/Assets/Resources/LocalTranslationAssets/vocab.json" \
  "$PROJECT_PATH/Assets/Resources/LocalTranslationAssets/marian-reference.json" \
  "$PROJECT_PATH/Assets/LocalTokenizerRuntime/PhraseLayer.Tokenization.Microsoft.dll" \
  "$PROJECT_PATH/Assets/LocalTokenizerRuntime/Microsoft.ML.Tokenizers.dll" \
  "$PROJECT_PATH/Assets/LocalTokenizerRuntime/Google.Protobuf.dll" \
  "$PROJECT_PATH/Assets/LocalTokenizerRuntime/link.xml"; do
  [[ -s "$required" ]] || { echo "Required combined Read Mode + Marian fixture asset is missing or empty: $required" >&2; exit 2; }
done

[[ -s "$PHRASELAYER_JAPANESE_FONT_SOURCE" ]] || { echo "Reviewed Japanese font source is missing or empty." >&2; exit 2; }
mkdir -p "$(dirname "$PHRASELAYER_READ_MODE_MARIAN_PRODUCT_APK_PATH")"

# A clean checkout does not commit generated Meta XR project settings. Apply the pinned Meta SDK's Required Android
# fixes in a dedicated Unity process, then build from a fresh second process after those settings are persisted.
unset PHRASELAYER_META_PROJECT_SETUP_APPLIED
"$UNITY_EDITOR" \
  -batchmode \
  -nographics \
  -projectPath "$PROJECT_PATH" \
  -executeMethod PhraseLayer.Unity.Editor.PhraseLayerQuestProjectSetup.ApplyAndroidRequiredFixesBatch \
  -logFile -
export PHRASELAYER_META_PROJECT_SETUP_APPLIED=1

# Packaging-only process. Real PP-OCR GPU inference/preprocess parity and real Marian exact-token/Read-Mode parity
# must run as separate host gates before this build. -nographics is intentional because this process performs no
# model inference and no camera, MRUK, Android runtime, or Quest execution.
"$UNITY_EDITOR" \
  -batchmode \
  -nographics \
  -projectPath "$PROJECT_PATH" \
  -executeMethod PhraseLayer.Unity.Editor.PhraseLayerReadModeMarianProductAndroidBuild.BuildBatch \
  -logFile -

EVIDENCE="$(dirname "$PHRASELAYER_READ_MODE_MARIAN_PRODUCT_APK_PATH")/PhraseLayer.read-mode-marian-product-build-evidence.json"
[[ -s "$PHRASELAYER_READ_MODE_MARIAN_PRODUCT_APK_PATH" ]] || { echo "Combined Read Mode + Marian product APK was not produced." >&2; exit 3; }
[[ -s "$EVIDENCE" ]] || { echo "Combined Read Mode + Marian product build evidence was not produced: $EVIDENCE" >&2; exit 4; }

echo "PASS: Meta Quest project setup + local-only combined Read Mode + Marian Android ARM64 IL2CPP packaging fixture; no Quest/runtime PASS is implied."
