param([ValidateSet('Debug','Release')][string]$Configuration)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'verify-stage3-feature.ps1') @PSBoundParameters -Suites source-mappings
