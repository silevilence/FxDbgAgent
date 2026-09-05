param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'publish-mcp.ps1') -Configuration $Configuration
$bundle = Join-Path $repoRoot "artifacts/mcp/$Configuration"
dotnet (Join-Path $repoRoot "tests/FxDbg.McpIntegrationTests/bin/$Configuration/net10.0-windows/FxDbg.McpIntegrationTests.dll") $bundle
if ($LASTEXITCODE -ne 0) { throw 'Official MCP client test failed.' }
node (Join-Path $PSScriptRoot 'test-mcp-protocol.mjs') $bundle
if ($LASTEXITCODE -ne 0) { throw 'Raw stdio protocol test failed.' }
Write-Host "Stage 2-1 $Configuration passed."
