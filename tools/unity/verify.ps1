$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$Project = Join-Path $Root "unity/PhraseLayer.Unity"
if (-not $env:UNITY_EDITOR) {
    throw "UNITY_EDITOR must point to Unity.exe (or the Unity Editor executable on this platform)."
}
& $env:UNITY_EDITOR -batchmode -nographics -projectPath $Project -executeMethod "PhraseLayer.Unity.Editor.PhraseLayerEditorVerification.VerifyCorePipelineBatch" -logFile -
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
