param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'verify-stage2-2a.ps1') -Configuration $Configuration
dotnet (Join-Path $repoRoot "tests/FxDbg.McpIntegrationTests/bin/$Configuration/net10.0-windows/FxDbg.McpIntegrationTests.dll") (Join-Path $repoRoot "artifacts/mcp/$Configuration") execution $repoRoot $Configuration
if ($LASTEXITCODE -ne 0) { throw 'MCP execution matrix failed.' }
