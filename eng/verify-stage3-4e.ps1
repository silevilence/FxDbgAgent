param([ValidateSet('Debug','Release')][string]$Configuration)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'validation-reuse.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$report = Join-Path $repoRoot 'artifacts/stage3-4-validation'
$null = New-Item -ItemType Directory -Path $report -Force
if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run the IIS lifecycle matrix in elevated 64-bit PowerShell.' }
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
        Run-Check "lifecycle-build-$current" $shell @('-NoProfile','-File',(Join-Path $PSScriptRoot 'publish-mcp.ps1'),'-Configuration',$current) 600
        $env:DOTNET_CLI_HOME=Join-Path $repoRoot 'artifacts/dotnet-home'
        $env:NUGET_PACKAGES=Join-Path $repoRoot 'artifacts/nuget-packages'
        Run-Check "lifecycle-unit-$current" 'dotnet' @('test',(Join-Path $repoRoot 'tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj'),'-c',$current,'--no-build','--no-restore') 180
        $setup=@('-NoProfile','-File',(Join-Path $PSScriptRoot 'setup-stage3-4-environment.ps1'),'-EnvironmentName','Stage3-4-lifecycle','-PortBase','58352','-Configuration',$current)
        $manifest=Join-Path $repoRoot 'artifacts/stage3-4-environment-lifecycle/state.json'
        try {
            Run-Check "lifecycle-install-$current" $shell ($setup+@('-Action','Install')) 240
            Run-Check "lifecycle-mcp-$current" 'dotnet' @((Join-Path $repoRoot "tests/FxDbg.McpIntegrationTests/bin/$current/net10.0-windows/FxDbg.McpIntegrationTests.dll"),
                (Join-Path $repoRoot "artifacts/mcp/$current"),'iis-lifecycle',$repoRoot,$current,$manifest) 420
            Run-Check "lifecycle-health-$current" $shell ($setup+@('-Action','Verify')) 120
        }
        finally {
            if (Test-Path -LiteralPath $manifest) { Run-Check "lifecycle-cleanup-$current" $shell ($setup+@('-Action','Remove')) 120 }
        }
    }
    $passed=$true
}
finally { @{passed=$passed;outcomes=@($outcomes.ToArray());atUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $report 'lifecycle-result.json') -Encoding utf8 }
