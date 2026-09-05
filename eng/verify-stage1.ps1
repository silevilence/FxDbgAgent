param([ValidateSet('Debug','Release')][string]$Configuration)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$report = Join-Path $repoRoot 'artifacts/stage1-validation'
$null = New-Item -ItemType Directory -Path $report -Force
$configurations = if ($Configuration) { @($Configuration) } else { @('Debug','Release') }
foreach ($current in $configurations) {
    Start-Transcript -Path (Join-Path $report "$current.log") -Force
    try { & (Join-Path $PSScriptRoot 'verify-stage1-12.ps1') -Configuration $current }
    finally { Stop-Transcript }
}
Write-Host "Complete phase 1 regression passed for $($configurations -join ' and ')."
