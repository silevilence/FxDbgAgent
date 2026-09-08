param([ValidateSet('Debug','Release')][string]$Configuration, [string]$NodePath = 'node', [string]$CodePath = 'code')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'validation-reuse.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$report = Join-Path $repoRoot 'artifacts/stage3-4-validation'
$null = New-Item -ItemType Directory -Path $report -Force
if (-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run the Service matrix in elevated 64-bit PowerShell.' }
if (-not ('FxDbg.Validation.ValidationProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ValidationProcess.cs') }
$shell = (Get-Process -Id $PID).Path
$CodePath = & (Join-Path $PSScriptRoot 'find-vscode.ps1') -CodePath $CodePath
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
        Run-Check "services-build-$current" $shell @('-NoProfile','-File',(Join-Path $PSScriptRoot 'publish-mcp.ps1'),'-Configuration',$current) 600
        Run-Check "services-dap-build-$current" $shell @('-NoProfile','-File',(Join-Path $PSScriptRoot 'publish-dap.ps1'),'-Configuration',$current) 600
        $env:DOTNET_CLI_HOME=Join-Path $repoRoot 'artifacts/dotnet-home'
        $env:NUGET_PACKAGES=Join-Path $repoRoot 'artifacts/nuget-packages'
        Run-Check "services-unit-$current" 'dotnet' @('test',(Join-Path $repoRoot 'tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj'),'-c',$current,'--no-build','--no-restore') 180
        $setup=@('-NoProfile','-File',(Join-Path $PSScriptRoot 'setup-stage3-4-environment.ps1'),'-EnvironmentName','Stage3-4-services','-PortBase','58348','-Configuration',$current)
        $manifest=Join-Path $repoRoot 'artifacts/stage3-4-environment-services/state.json'
        try {
            Run-Check "services-install-$current" $shell ($setup+@('-Action','Install')) 240
            Run-Check "services-mcp-$current" 'dotnet' @((Join-Path $repoRoot "tests/FxDbg.McpIntegrationTests/bin/$current/net10.0-windows/FxDbg.McpIntegrationTests.dll"),
                (Join-Path $repoRoot "artifacts/mcp/$current"),'services',$repoRoot,$current,$manifest) 360
            Run-Check "services-dap-$current" $NodePath @((Join-Path $repoRoot 'tests/FxDbg.DapTests/service-iis.js'),(Join-Path $repoRoot "artifacts/dap/$current"),$repoRoot,$current,$manifest) 180
            $env:FXDBG_TEST_ROOT=$repoRoot
            $env:FXDBG_TEST_CONFIGURATION=$current
            $env:FXDBG_TEST_BUNDLE=Join-Path $repoRoot "artifacts/dap/$current"
            $env:FXDBG_DOTNET=(Get-Command dotnet).Source
            $env:FXDBG_ENVIRONMENT_MANIFEST=$manifest
            $clientDirectory=Join-Path $report ('vscode-profile-'+[Guid]::NewGuid().ToString('N'))
            $clientEvidence=Join-Path $report "vscode-service-iis-$current.json"
            if(Test-Path -LiteralPath $clientEvidence) { Remove-Item -LiteralPath $clientEvidence }
            Run-Check "services-vscode-$current" $CodePath @('--disable-extensions','--disable-updates','--skip-welcome','--skip-release-notes','--disable-workspace-trust',
                '--user-data-dir',(Join-Path $clientDirectory 'data'),'--extensions-dir',(Join-Path $clientDirectory 'extensions'),
                "--extensionDevelopmentPath=$(Join-Path $repoRoot 'extensions/fxdbg')","--extensionTestsPath=$(Join-Path $repoRoot 'tests/FxDbg.DapTests/vscode-service-iis.js')") 300
            $clientResult=Get-Content -LiteralPath $clientEvidence -Raw | ConvertFrom-Json
            if(-not $clientResult.passed -or $clientResult.cases.Count -ne 4) { throw 'Real VS Code Service/IIS evidence is incomplete.' }
            Run-Check "services-health-$current" $shell ($setup+@('-Action','Verify')) 120
        }
        finally {
            if (Test-Path -LiteralPath $manifest) { Run-Check "services-cleanup-$current" $shell ($setup+@('-Action','Remove')) 120 }
        }
    }
    $passed=$true
}
finally { @{passed=$passed;outcomes=@($outcomes.ToArray());atUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $report 'services-result.json') -Encoding utf8 }
