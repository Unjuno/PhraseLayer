$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$Project = Join-Path $Root "unity/PhraseLayer.Unity"
$UnityLog = if ($env:PHRASELAYER_UNITY_LOG) { $env:PHRASELAYER_UNITY_LOG } else { Join-Path $Root ".ci/unity-real.log" }
$UnityTimeout = if ($env:PHRASELAYER_UNITY_TIMEOUT_SECONDS) { $env:PHRASELAYER_UNITY_TIMEOUT_SECONDS } else { "900" }

if (-not $env:UNITY_EDITOR) {
    throw "UNITY_EDITOR must point to Unity.exe (or the Unity Editor executable on this platform)."
}

$LogDirectory = Split-Path -Parent $UnityLog
if ($LogDirectory) {
    New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
}

python (Join-Path $Root "tools/unity/run_unity_batch.py") `
    --unity-editor $env:UNITY_EDITOR `
    --project $Project `
    --execute-method "PhraseLayer.Unity.Editor.PhraseLayerEditorVerification.VerifyCorePipelineBatch" `
    --log-file $UnityLog `
    --timeout-seconds $UnityTimeout
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
