param([ValidateSet('Debug','Release')][string]$Configuration, [string[]]$Suites)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'validation-reuse.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$report = Join-Path $repoRoot 'artifacts/stage3-validation'
$null = New-Item -ItemType Directory -Path $report -Force
if (-not ('FxDbg.Validation.ValidationProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ValidationProcess.cs') }
$shell = (Get-Process -Id $PID).Path
$configurations = if ($Configuration) { @($Configuration) } else { @('Debug','Release') }
foreach ($current in $configurations) {
    $code = (Invoke-ValidationProcess -Executable ($shell) -Arguments (@('-NoProfile','-File',(Join-Path $PSScriptRoot 'publish-mcp.ps1'),'-Configuration',$current)) -Directory ($repoRoot) -Log ((Join-Path $report "feature-build-$current.log")) -Seconds (600))
    if ($code -ne 0) { throw "Feature build failed: $current ($code)" }
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot 'artifacts/dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $repoRoot 'artifacts/nuget-packages'
    $code = (Invoke-ValidationProcess -Executable ('dotnet') -Arguments (@('test',(Join-Path $repoRoot 'tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj'),'-c',$current,'--no-build','--no-restore')) -Directory ($repoRoot) -Log ((Join-Path $report "feature-unit-$current.log")) -Seconds (120))
    if ($code -ne 0) { throw "Feature unit tests failed: $current ($code)" }
    foreach ($suite in $Suites) {
    $code = (Invoke-ValidationProcess -Executable ('dotnet') -Arguments (@((Join-Path $repoRoot "tests/FxDbg.McpIntegrationTests/bin/$current/net10.0-windows/FxDbg.McpIntegrationTests.dll"),
          (Join-Path $repoRoot "artifacts/mcp/$current"),$suite,$repoRoot,$current)) -Directory ($repoRoot) -Log ((Join-Path $report "${suite}-mcp-$current.log")) -Seconds (300))
    Get-Content (Join-Path $report "${suite}-mcp-$current.log") | Write-Host
    if ($code -ne 0) { throw "Feature MCP tests failed: $current ($code)" }
}
}
