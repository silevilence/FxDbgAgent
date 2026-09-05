$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$report = Join-Path $repoRoot 'artifacts/stage1-validation'
$null = New-Item -ItemType Directory -Path $report -Force
foreach ($configuration in @('Debug','Release')) {
    Start-Transcript -Path (Join-Path $report "$configuration.log") -Force
    try { & (Join-Path $PSScriptRoot 'verify-stage1-12.ps1') -Configuration $configuration }
    finally { Stop-Transcript }
}
Write-Host 'Complete phase 1 regression passed for Debug and Release.'
