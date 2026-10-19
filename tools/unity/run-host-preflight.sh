#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="${PHRASELAYER_UNITY_PROJECT_PATH:-$ROOT/unity/PhraseLayer.Unity}"
UNITY_EDITOR="${UNITY_EDITOR:-}"
EVIDENCE="${PHRASELAYER_UNITY_HOST_PREFLIGHT_EVIDENCE_PATH:-$PROJECT/Temp/PhraseLayer.unity-host-preflight.json}"

if [[ -z "$UNITY_EDITOR" ]]; then
  echo "UNITY_EDITOR must point to the Unity 6000.0.66f2 Editor executable." >&2
  exit 2
fi
if [[ ! -x "$UNITY_EDITOR" ]]; then
  echo "UNITY_EDITOR is not executable: $UNITY_EDITOR" >&2
  exit 2
fi
if [[ ! -s "$PROJECT/Packages/manifest.json" ]]; then
  echo "Unity package manifest is missing: $PROJECT/Packages/manifest.json" >&2
  exit 2
fi
if [[ ! -s "$PROJECT/ProjectSettings/ProjectVersion.txt" ]]; then
  echo "Unity ProjectVersion.txt is missing: $PROJECT/ProjectSettings/ProjectVersion.txt" >&2
  exit 2
fi

mkdir -p "$(dirname "$EVIDENCE")"
rm -f "$EVIDENCE"
export PHRASELAYER_UNITY_HOST_PREFLIGHT_EVIDENCE_PATH="$EVIDENCE"

# No GPU inference or device interaction is performed here, so -nographics is appropriate.
# The Editor method itself verifies exact Unity version, Android build support and the reviewed inference compile gate.
"$UNITY_EDITOR" \
  -batchmode \
  -nographics \
  -projectPath "$PROJECT" \
  -executeMethod PhraseLayer.Unity.Editor.PhraseLayerUnityHostPreflight.RunBatch \
  -logFile -

test -s "$EVIDENCE" || { echo "Unity host preflight evidence was not produced: $EVIDENCE" >&2; exit 3; }
printf 'PASS: real Unity host capability preflight; evidence=%s\n' "$EVIDENCE"
