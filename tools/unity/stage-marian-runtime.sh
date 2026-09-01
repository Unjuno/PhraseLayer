#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SOURCE_DIR="${PHRASELAYER_MARIAN_SOURCE_DIR:-}"
WORK_DIR="${PHRASELAYER_MARIAN_WORK_DIR:-}"
REVISION="a863894cdd2b80f3bc1c5966734aee9ffec207d1"

if [[ -z "$SOURCE_DIR" || ! -d "$SOURCE_DIR" ]]; then
  echo "PHRASELAYER_MARIAN_SOURCE_DIR must point to the exact pinned local Marian source snapshot." >&2
  exit 2
fi
if [[ -z "$WORK_DIR" ]]; then
  echo "PHRASELAYER_MARIAN_WORK_DIR must point to a writable temporary directory." >&2
  exit 2
fi

mkdir -p "$WORK_DIR"
EXPORT_DIR="$WORK_DIR/onnx"
EXPORT_MANIFEST="$WORK_DIR/marian-export.json"
TOKENIZER_MANIFEST="$WORK_DIR/marian-tokenizer-assets.json"
TOKENIZER_RUNTIME_MANIFEST="$WORK_DIR/marian-tokenizer-runtime.json"

rm -rf "$EXPORT_DIR"
mkdir -p "$EXPORT_DIR"

python "$ROOT/tools/export_marian_onnx.py" \
  --source-dir "$SOURCE_DIR" \
  --output-dir "$EXPORT_DIR" \
  --repository-root "$ROOT" \
  --lock "$ROOT/models/models.lock.json" \
  --output-manifest "$EXPORT_MANIFEST" \
  --execute

python "$ROOT/tools/prepare_unity_marian_onnx_assets.py" \
  --export-dir "$EXPORT_DIR" \
  --export-manifest "$EXPORT_MANIFEST"

python "$ROOT/tools/prepare_unity_marian_tokenizer_assets.py" \
  --snapshot-dir "$SOURCE_DIR" \
  --revision "$REVISION" \
  --destination "$ROOT/unity/PhraseLayer.Unity/Assets/Resources/LocalTranslationAssets" \
  --manifest "$TOKENIZER_MANIFEST"

dotnet restore "$ROOT/src/PhraseLayer.Tokenization.Microsoft/PhraseLayer.Tokenization.Microsoft.csproj"
dotnet build "$ROOT/src/PhraseLayer.Tokenization.Microsoft/PhraseLayer.Tokenization.Microsoft.csproj" \
  -c Release \
  --no-restore

python "$ROOT/tools/prepare_unity_tokenizer_runtime.py" \
  --build-output "$ROOT/src/PhraseLayer.Tokenization.Microsoft/bin/Release/netstandard2.1" \
  --destination "$ROOT/unity/PhraseLayer.Unity/Assets/LocalTokenizerRuntime" \
  --manifest "$TOKENIZER_RUNTIME_MANIFEST"

printf 'PASS: staged pinned Marian Unity runtime\n'
printf '  revision=%s\n' "$REVISION"
printf '  export_manifest=%s\n' "$EXPORT_MANIFEST"
printf '  tokenizer_manifest=%s\n' "$TOKENIZER_MANIFEST"
printf '  tokenizer_runtime_manifest=%s\n' "$TOKENIZER_RUNTIME_MANIFEST"
