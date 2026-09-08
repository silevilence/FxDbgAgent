param([ValidateSet('Debug','Release')][string]$Configuration,[switch]$CollectCoverage)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'validation-reuse.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$report = Join-Path $repoRoot 'artifacts/stage3-4-validation'
$null = New-Item -ItemType Directory -Path $report -Force
if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run the permission matrix in elevated 64-bit PowerShell.' }
if (-not ('FxDbg.Validation.ValidationProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ValidationProcess.cs') }
$shell = (Get-Process -Id $PID).Path
$configurations = if ($Configuration) { @($Configuration) } else { @('Debug','Release') }
$outcomes = [Collections.Generic.List[object]]::new()
function Run-Check([string]$Name,[string]$Executable,[string[]]$Arguments,[int]$Seconds) {
    $log = Join-Path $report "$Name.log"
    $exit = (Invoke-ValidationProcess -Executable ($Executable) -Arguments ($Arguments) -Directory ($repoRoot) -Log ($log) -Seconds ($Seconds))
    $outcomes.Add(@{name=$Name;exitCode=$exit;log=$log})
    if ($exit -ne 0) { throw "$Name failed ($exit): $log" }
}
$passed = $false
try {
    foreach ($current in $configurations) {
        Run-Check "permissions-build-$current" $shell @('-NoProfile','-File',(Join-Path $PSScriptRoot 'publish-mcp.ps1'),'-Configuration',$current) 600
        $env:DOTNET_CLI_HOME=Join-Path $repoRoot 'artifacts/dotnet-home'
        $env:NUGET_PACKAGES=Join-Path $repoRoot 'artifacts/nuget-packages'
        Run-Check "permissions-unit-$current" 'dotnet' @('test',(Join-Path $repoRoot 'tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj'),'-c',$current,'--no-build','--no-restore') 180
        $setup=@('-NoProfile','-File',(Join-Path $PSScriptRoot 'setup-stage3-4-environment.ps1'),'-EnvironmentName','Stage3-4-permissions','-PortBase','58344','-Configuration',$current)
        $manifest=Join-Path $repoRoot 'artifacts/stage3-4-environment-permissions/state.json'
        try {
            Run-Check "permissions-install-$current" $shell ($setup+@('-Action','Install')) 240
            Run-Check "permissions-mcp-$current" 'dotnet' @((Join-Path $repoRoot "tests/FxDbg.McpIntegrationTests/bin/$current/net10.0-windows/FxDbg.McpIntegrationTests.dll"),
                (Join-Path $repoRoot "artifacts/mcp/$current"),'permissions',$repoRoot,$current,$manifest) 240
            if($CollectCoverage) {
                Run-Check "permissions-coverage-$current" $shell @('-NoProfile','-File',(Join-Path $PSScriptRoot 'collect-stage3-4-coverage.ps1'),'-Suite','permissions','-Configuration',$current) 600
            }
            Run-Check "permissions-health-$current" $shell ($setup+@('-Action','Verify')) 120
        }
        finally {
            if (Test-Path -LiteralPath $manifest) { Run-Check "permissions-cleanup-$current" $shell ($setup+@('-Action','Remove')) 120 }
        }
    }
    $passed=$true
}
finally { @{passed=$passed;outcomes=@($outcomes.ToArray());atUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $report 'permissions-result.json') -Encoding utf8 }
