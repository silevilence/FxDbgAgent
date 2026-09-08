param([ValidateSet('Debug','Release')][string]$Configuration)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'validation-reuse.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$report = Join-Path $repoRoot 'artifacts/stage2-validation'
$null = New-Item -ItemType Directory -Path $report -Force
$env:DOTNET_CLI_HOME = Join-Path $repoRoot 'artifacts/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot 'artifacts/nuget-packages'
if (-not ('FxDbg.Validation.ValidationProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ValidationProcess.cs') }
$outcomes = [Collections.Generic.List[object]]::new()
function Get-ValidationInputs {
    foreach ($directory in @('src','tests','eng')) {
        Get-ChildItem -LiteralPath (Join-Path $repoRoot $directory) -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs','.csproj','.props','.targets','.ps1','.mjs') } |
            Sort-Object FullName | ForEach-Object { "$([IO.Path]::GetRelativePath($repoRoot,$_.FullName)) $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }
    }
    foreach ($file in @('global.json','Directory.Build.props','Directory.Build.targets','FxDbg.sln')) {
        "$file $((Get-FileHash -LiteralPath (Join-Path $repoRoot $file) -Algorithm SHA256).Hash)"
    }
}
$inputs = @(Get-ValidationInputs)
$inputs | Set-Content -LiteralPath (Join-Path $report 'stage2-inputs.sha256') -Encoding utf8
$shell = (Get-Process -Id $PID).Path
function Invoke-Validation([string]$Name, [string]$Executable, [string[]]$Arguments, [int]$Seconds) {
    $log = Join-Path $report "$Name.log"
    Write-Host "Running $Name (deadline ${Seconds}s)..."
    $code = (Invoke-ValidationProcess -Executable ($Executable) -Arguments ($Arguments) -Directory ($repoRoot) -Log ($log) -Seconds ($Seconds))
    Get-Content -LiteralPath $log | Write-Host
    $outcomes.Add(@{ name = $Name; exitCode = $code; log = $log })
    if ($code -ne 0) { throw "$Name failed with exit $code. See $log" }
}
$passed = $false
$validationRun = Enter-ValidationRun
try {
    Initialize-ValidationBuilds $validationRun $Configuration
    Invoke-Validation 'stage2-3-install-evidence' $shell @('-NoProfile','-File',(Join-Path $PSScriptRoot 'verify-stage2-3.ps1')) 120
    $configurations = if ($Configuration) { @($Configuration) } else { @('Debug','Release') }
    foreach ($current in $configurations) {
        # Each suite has its own cancellation/cleanup; supervisor also bounds native/build hangs.
        Invoke-Validation "stage2-$current" $shell @('-NoProfile','-File',(Join-Path $PSScriptRoot 'verify-stage2-4.ps1'),'-Configuration',$current) 900
        Invoke-Validation "stage2-mvp-$current" 'dotnet' @((Join-Path $repoRoot "tests/FxDbg.McpIntegrationTests/bin/$current/net10.0-windows/FxDbg.McpIntegrationTests.dll"),(Join-Path $repoRoot "artifacts/mcp/$current"),'mvp',$repoRoot,$current) 360
    }
    $stage1Arguments = @('-NoProfile','-File',(Join-Path $PSScriptRoot 'verify-stage1.ps1'))
    if ($Configuration) { $stage1Arguments += @('-Configuration',$Configuration) }
    Invoke-Validation 'stage1-full-regression' $shell $stage1Arguments 1800
    if (Compare-Object $inputs @(Get-ValidationInputs)) { throw 'Source/build/test inputs changed during validation; rerun before marking acceptance passed.' }
    Confirm-ValidationRun $validationRun
    $passed = $true
}
finally {
    Exit-ValidationRun $validationRun
    @{ passed = $passed; configuration = $(if ($Configuration) { $Configuration } else { 'Debug+Release' }); atUtc = [DateTimeOffset]::UtcNow.ToString('o'); outcomes = @($outcomes.ToArray()) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $report 'stage2-result.json') -Encoding utf8
}
Write-Host 'Phase 2 MCP and phase 1 regressions passed; independent Agent evidence verified.'
