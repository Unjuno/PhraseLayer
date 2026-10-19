#!/usr/bin/env bash
set -euo pipefail

: "${UNITY_EDITOR:?UNITY_EDITOR must point to the Unity 6000.0.66f2 Editor executable.}"
: "${PHRASELAYER_MARIAN_PRODUCT_FIXTURE_APK_PATH:?PHRASELAYER_MARIAN_PRODUCT_FIXTURE_APK_PATH must name a local APK output path.}"

PROJECT_PATH="${PHRASELAYER_UNITY_PROJECT_PATH:-unity/PhraseLayer.Unity}"

for required in \
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
  test -s "$required" || { echo "Required Marian Android fixture asset is missing or empty: $required" >&2; exit 2; }
done

mkdir -p "$(dirname "$PHRASELAYER_MARIAN_PRODUCT_FIXTURE_APK_PATH")"

# Build-only process: exact GPU translation parity runs in verify-local-marian-translation.sh immediately before
# this gate. -nographics is intentional here because no model inference is executed during Android packaging.
"$UNITY_EDITOR" \
  -batchmode \
  -nographics \
  -projectPath "$PROJECT_PATH" \
  -executeMethod PhraseLayer.Unity.Editor.PhraseLayerMarianProductAndroidBuild.BuildBatch \
  -quit \
  -logFile -

EVIDENCE="$(dirname "$PHRASELAYER_MARIAN_PRODUCT_FIXTURE_APK_PATH")/PhraseLayer.marian-product-fixture-build-evidence.json"
test -s "$PHRASELAYER_MARIAN_PRODUCT_FIXTURE_APK_PATH" || { echo "Marian product fixture APK was not produced." >&2; exit 3; }
test -s "$EVIDENCE" || { echo "Marian product fixture build evidence was not produced: $EVIDENCE" >&2; exit 4; }

echo "PASS: built local-only Marian Android ARM64 IL2CPP product translation fixture; APK must not be uploaded while redistribution review is pending."
