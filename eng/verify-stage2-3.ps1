$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'verify-skill-install.ps1')
& (Join-Path $PSScriptRoot 'verify-agent-evidence.ps1')
Write-Host 'Stage 2-3 skill install and recorded independent Agent acceptance passed.'
