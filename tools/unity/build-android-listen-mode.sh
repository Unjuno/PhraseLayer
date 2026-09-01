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

OUTPUT="${PHRASELAYER_ANDROID_BUILD_PATH:-$ROOT/Builds/Android/PhraseLayer.apk}"
export PHRASELAYER_ANDROID_BUILD_PATH="$OUTPUT"

"$UNITY_EDITOR" \
  -batchmode \
  -nographics \
  -projectPath "$PROJECT" \
  -executeMethod PhraseLayer.Unity.Editor.PhraseLayerAndroidBuild.BuildBatch \
  -logFile -

if [[ ! -f "$OUTPUT" ]]; then
  echo "Unity returned success but APK is missing: $OUTPUT" >&2
  exit 3
fi
EVIDENCE="$(dirname "$OUTPUT")/PhraseLayer.android-build-evidence.json"
if [[ ! -f "$EVIDENCE" ]]; then
  echo "Unity returned success but Android build evidence is missing: $EVIDENCE" >&2
  exit 3
fi
printf 'PASS: Android ARM64 IL2CPP APK=%s evidence=%s\n' "$OUTPUT" "$EVIDENCE"
