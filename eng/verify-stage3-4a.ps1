param([ValidateSet('Debug','Release')][string]$Configuration)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$report = Join-Path $repoRoot 'artifacts/stage3-4-validation'
$null = New-Item -ItemType Directory -Path $report -Force
$env:DOTNET_CLI_HOME = Join-Path $repoRoot 'artifacts/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot 'artifacts/nuget-packages'
if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The real Service/IIS environment test requires an elevated 64-bit PowerShell.'
}
if (-not ('FxDbg.Validation.ValidationProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ValidationProcess.cs') }
$shell = (Get-Process -Id $PID).Path
$installer = Join-Path $PSScriptRoot 'setup-stage3-4-environment.ps1'
$environmentName = 'Stage3-4-check'
$deployment = Join-Path $env:ProgramData "FxDbgAgent/$environmentName"
$manifest = Join-Path $repoRoot 'artifacts/stage3-4-environment-check/state.json'
$outcomes = [Collections.Generic.List[object]]::new()
$configurations = if ($Configuration) { @($Configuration) } else { @('Debug','Release') }
function Run-Setup([string]$Action,[string]$Config,[string]$Name,[string[]]$Extra=@(),[int]$Expected=0) {
    $arguments = @('-NoProfile','-File',$installer,'-Action',$Action,'-Configuration',$Config,'-EnvironmentName',$environmentName,'-PortBase','58342') + $Extra
    $log = Join-Path $report "$Name.log"
    $code = [FxDbg.Validation.ValidationProcess]::Run($shell,$arguments,$repoRoot,$log,240)
    $outcomes.Add(@{name=$Name;exitCode=$code;expected=$Expected;log=$log})
    if ($code -ne $Expected) { throw "$Name failed with $code; see $log" }
}
function Assert-Clean {
    if (Test-Path -LiteralPath $deployment) { throw 'Deployment files remain after removal.' }
    if (Get-Service 'FxDbgStage34-check-*' -ErrorAction SilentlyContinue) { throw 'Test services remain after removal.' }
    $manager = [Microsoft.Web.Administration.ServerManager]::new()
    try {
        if (@($manager.Sites | Where-Object Name -Like 'FxDbgStage34-check-*').Count -or
            @($manager.ApplicationPools | Where-Object Name -Like 'FxDbgStage34-check-*').Count) { throw 'Test IIS resources remain after removal.' }
    } finally { $manager.Dispose() }
}
Add-Type -Path (Join-Path $env:windir 'System32/inetsrv/Microsoft.Web.Administration.dll')
$baselinePath = Join-Path $env:windir 'System32/inetsrv/config/applicationHost.config'
$baseline = [IO.File]::ReadAllText($baselinePath)
$passed = $false
try {
    Assert-Clean
    Run-Setup 'Preflight' 'Debug' 'environment-preflight'
    foreach ($current in $configurations) {
        foreach ($project in @('Fx40.Environment.Service.x86','Fx40.Environment.Service.x64','Fx40.Environment.Web','Fx40.Environment.Late')) {
            $code = [FxDbg.Validation.ValidationProcess]::Run('dotnet',
                @('build',(Join-Path $repoRoot "tests/Debuggees/$project/$project.csproj"),'-c',$current,'--nologo'),
                $repoRoot,(Join-Path $report "$project-$current-build.log"),180)
            if ($code -ne 0) { throw "Build failed: $project $current" }
        }
        $code = [FxDbg.Validation.ValidationProcess]::Run('dotnet',
            @('test',(Join-Path $repoRoot 'tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj'),'-c',$current,
              '--filter','FullyQualifiedName~Service_and_iis_fixtures','--nologo'),
            $repoRoot,(Join-Path $report "environment-symbols-$current.log"),180)
        if ($code -ne 0) { throw "Fixture DLL/PDB identity tests failed: $current" }
        try {
            Run-Setup 'Install' $current "environment-install-$current"
            $state = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
            foreach ($resource in $state.resources) {
                $initial = Invoke-RestMethod $resource.url -TimeoutSec 10
                if ($initial.lateLoaded) { throw 'Late module was loaded before its first request.' }
                $late = Invoke-RestMethod ($resource.url+'?action=late') -TimeoutSec 10
                if ($late.lateResult -ne 85 -or -not $late.lateLoaded) { throw 'IIS late module did not execute.' }
                'go' | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $resource.heartbeat) 'late.request')
                $deadline = [DateTime]::UtcNow.AddSeconds(10)
                $heartbeat = $null
                do {
                    Start-Sleep -Milliseconds 200
                    try { $heartbeat = Get-Content -LiteralPath $resource.heartbeat -Raw | ConvertFrom-Json } catch { continue }
                } until ($heartbeat.lateResult -eq 85 -or [DateTime]::UtcNow -ge $deadline)
                if ($heartbeat.lateResult -ne 85) { throw 'Service late module did not execute.' }
            }
            Run-Setup 'Verify' $current "environment-verify-$current"
        }
        finally { if (Test-Path -LiteralPath $manifest) { Run-Setup 'Remove' $current "environment-remove-$current" } }
        Assert-Clean
    }
    try { Run-Setup 'Install' $configurations[0] 'environment-interrupted' @('-InterruptAt','AfterFirstSite') 197 }
    finally { if (Test-Path -LiteralPath $manifest) { Run-Setup 'Remove' $configurations[0] 'environment-recover-interrupted' } }
    Assert-Clean
    if ([IO.File]::ReadAllText($baselinePath) -ne $baseline) { throw 'IIS configuration did not return exactly to its baseline.' }
    $passed = $true
}
finally {
    @{passed=$passed;configurations=$configurations;outcomes=@($outcomes.ToArray());atUtc=[DateTimeOffset]::UtcNow.ToString('o')} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $report 'environment-result.json') -Encoding utf8
}
Write-Host 'Stage 3-4a: deployment, late load, cleanup, abrupt interruption recovery and IIS baseline passed.'
