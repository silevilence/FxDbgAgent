param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'verify-stage2-1.ps1') -Configuration $Configuration
dotnet (Join-Path $repoRoot "tests/FxDbg.McpIntegrationTests/bin/$Configuration/net10.0-windows/FxDbg.McpIntegrationTests.dll") (Join-Path $repoRoot "artifacts/mcp/$Configuration") observations $repoRoot $Configuration
if ($LASTEXITCODE -ne 0) { throw 'MCP observation matrix failed.' }
dotnet test (Join-Path $repoRoot 'tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj') -c $Configuration --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
