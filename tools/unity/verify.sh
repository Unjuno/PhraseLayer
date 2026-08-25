#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/unity/PhraseLayer.Unity"
UNITY_EDITOR="${UNITY_EDITOR:-}"
UNITY_LOG="${PHRASELAYER_UNITY_LOG:-$ROOT/.ci/unity-real.log}"
UNITY_TIMEOUT="${PHRASELAYER_UNITY_TIMEOUT_SECONDS:-900}"

if [[ -z "$UNITY_EDITOR" ]]; then
  echo "UNITY_EDITOR must point to the Unity Editor executable." >&2
  exit 2
fi

mkdir -p "$(dirname "$UNITY_LOG")"
python3 "$ROOT/tools/unity/run_unity_batch.py" \
  --unity-editor "$UNITY_EDITOR" \
  --project "$PROJECT" \
  --execute-method PhraseLayer.Unity.Editor.PhraseLayerEditorVerification.VerifyCorePipelineBatch \
  --log-file "$UNITY_LOG" \
  --timeout-seconds "$UNITY_TIMEOUT"
