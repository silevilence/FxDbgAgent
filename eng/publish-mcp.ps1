param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'publish-host.ps1') -Entry Mcp @PSBoundParameters
