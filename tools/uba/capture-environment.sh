#!/usr/bin/env bash
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/unity/PhraseLayer.Unity"

echo "PHRASELAYER_UBA_ENV_BEGIN"
echo "BUILD_NUMBER=${BUILD_NUMBER:-unknown}"
echo "BUILD_REVISION=${BUILD_REVISION:-unknown}"
echo "GIT_COMMIT=${GIT_COMMIT:-unknown}"
echo "GIT_BRANCH=${GIT_BRANCH:-unknown}"
echo "UNITY_VERSION=${UNITY_VERSION:-unknown}"
echo "BUILD_PLATFORM=${BUILD_PLATFORM:-unknown}"
echo "BUILD_TARGET=${BUILD_TARGET:-unknown}"
echo "BUILDCFG_TARGET=${BUILDCFG_TARGET:-unknown}"
echo "BUILDER_OS=${BUILDER_OS:-unknown}"
echo "CLEAN_BUILD=${CLEAN_BUILD:-unknown}"
echo "PROJECT_PATH=${PROJECT_PATH:-unknown}"
echo "BUILD_PATH=${BUILD_PATH:-unknown}"
echo "ANDROID_HOME=${ANDROID_HOME:-unknown}"
echo "ANDROID_NDK_ROOT=${ANDROID_NDK_ROOT:-unknown}"
echo "OUTPUT_DIRECTORY=${OUTPUT_DIRECTORY:-unknown}"

echo "--- git ---"
git -C "$ROOT" rev-parse HEAD 2>/dev/null || true
git --version 2>/dev/null || true

echo "--- ProjectVersion.txt ---"
cat "$PROJECT/ProjectSettings/ProjectVersion.txt" 2>/dev/null || true

echo "--- Packages/manifest.json ---"
cat "$PROJECT/Packages/manifest.json" 2>/dev/null || true

echo "--- Packages/packages-lock.json ---"
if [ -f "$PROJECT/Packages/packages-lock.json" ]; then
  cat "$PROJECT/Packages/packages-lock.json"
else
  echo "<not present before Unity package resolution>"
fi

echo "PHRASELAYER_UBA_ENV_END"
exit 0
