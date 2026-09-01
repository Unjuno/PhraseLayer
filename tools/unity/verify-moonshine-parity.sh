#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/unity/PhraseLayer.Unity"
UNITY_EDITOR="${UNITY_EDITOR:-}"
if [[ -z "$UNITY_EDITOR" ]]; then
  echo "UNITY_EDITOR must point to the Unity 6000.0.66f2 Editor executable." >&2
  exit 2
fi
for name in \
  PHRASELAYER_MOONSHINE_FIXTURE_WAV \
  PHRASELAYER_MOONSHINE_EXPECTED_TOKENS \
  PHRASELAYER_MOONSHINE_EXPECTED_TRANSCRIPT; do
  if [[ -z "${!name:-}" ]]; then
    echo "$name must point to a staged Moonshine parity input." >&2
    exit 2
  fi
done
"$UNITY_EDITOR" \
  -batchmode \
  -nographics \
  -projectPath "$PROJECT" \
  -executeMethod PhraseLayer.Unity.Editor.PhraseLayerLocalAsrAssets.RunFixtureTokenParityBatch \
  -logFile -
