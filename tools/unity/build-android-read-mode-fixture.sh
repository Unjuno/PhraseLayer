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
if [[ -z "${PHRASELAYER_JAPANESE_FONT_SOURCE:-}" ]]; then
  echo "PHRASELAYER_JAPANESE_FONT_SOURCE must point to a reviewed Japanese-capable .ttf or .otf file." >&2
  exit 2
fi
if [[ ! -f "$PHRASELAYER_JAPANESE_FONT_SOURCE" ]]; then
  echo "Reviewed Japanese font is missing: $PHRASELAYER_JAPANESE_FONT_SOURCE" >&2
  exit 2
fi

OUTPUT="${PHRASELAYER_READ_MODE_FIXTURE_APK_PATH:-$ROOT/Builds/Android/PhraseLayerReadModeFixture.apk}"
export PHRASELAYER_READ_MODE_FIXTURE_APK_PATH="$OUTPUT"

"$UNITY_EDITOR" \
  -batchmode \
  -nographics \
  -projectPath "$PROJECT" \
  -executeMethod PhraseLayer.Unity.Editor.PhraseLayerReadModeFixtureAndroidBuild.BuildBatch \
  -logFile -

if [[ ! -s "$OUTPUT" ]]; then
  echo "Unity returned success but Read Mode fixture APK is missing or empty: $OUTPUT" >&2
  exit 3
fi

EVIDENCE="$(dirname "$OUTPUT")/PhraseLayer.read-mode-fixture-build-evidence.json"
if [[ ! -s "$EVIDENCE" ]]; then
  echo "Unity returned success but Read Mode fixture build evidence is missing or empty: $EVIDENCE" >&2
  exit 3
fi

printf 'PASS: Read Mode fixture Android ARM64 IL2CPP APK=%s evidence=%s\n' "$OUTPUT" "$EVIDENCE"
