param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'artifacts/dap/fxdbg-0.1.0.vsix' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force
$env:npm_config_cache = Join-Path $repoRoot 'artifacts/npm-cache'
$env:npm_config_fetch_retries = '0'
$env:npm_config_fetch_timeout = '15000'
Push-Location (Join-Path $repoRoot 'extensions/fxdbg')
try {
    npx --yes '@vscode/vsce@3.6.2' package --no-dependencies --allow-missing-repository --skip-license -o $OutputPath
    if ($LASTEXITCODE -ne 0) { throw 'VSIX packaging failed.' }
}
finally { Pop-Location }
Write-Host "Extension package: $OutputPath"
