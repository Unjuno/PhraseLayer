#!/usr/bin/env bash
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"
mkdir -p .ci

python tools/validate_build_environment.py
python tools/validate_unity_compile_preflight.py
python tools/validate_unity_csharp9.py

dotnet restore tests/PhraseLayer.UnityShell.Compile/PhraseLayer.UnityShell.Compile.csproj
dotnet restore tests/PhraseLayer.UnityShell.Compile/PhraseLayer.UnityAndroid.Compile.csproj

set +e
dotnet build tests/PhraseLayer.UnityShell.Compile/PhraseLayer.UnityShell.Compile.csproj -c Release --no-restore 2>&1 | tee .ci/unity-editor-preflight.log
editor=${PIPESTATUS[0]}
dotnet build tests/PhraseLayer.UnityShell.Compile/PhraseLayer.UnityAndroid.Compile.csproj -c Release --no-restore 2>&1 | tee .ci/unity-android-preflight.log
android=${PIPESTATUS[0]}
set -e

echo "$editor" > .ci/unity-editor-preflight.exit
echo "$android" > .ci/unity-android-preflight.exit

python tools/extract_unity_compile_errors.py .ci/unity-editor-preflight.log || true
python tools/extract_unity_compile_errors.py .ci/unity-android-preflight.log || true

if [ "$editor" -ne 0 ] || [ "$android" -ne 0 ]; then
  echo "Unity compile preflight FAILED (Editor=$editor Android=$android)" >&2
  exit 1
fi

echo "Unity compile preflight PASS (Editor=0 Android=0)"
