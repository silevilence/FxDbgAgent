param([ValidateSet('Debug','Release')][string]$Configuration, [string]$NodePath = 'node', [string]$CodePath = 'code', [switch]$SkipServiceIis)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'validation-reuse.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$report = Join-Path $repoRoot 'artifacts/stage3-validation'
$null = New-Item -ItemType Directory -Path $report -Force
if (-not ('FxDbg.Validation.ValidationProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ValidationProcess.cs') }
$shell = (Get-Process -Id $PID).Path
$CodePath = & (Join-Path $PSScriptRoot 'find-vscode.ps1') -CodePath $CodePath
function Get-ValidationInputs {
    foreach ($directory in @('src','tests','eng','extensions','skills')) {
        Get-ChildItem -LiteralPath (Join-Path $repoRoot $directory) -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' -and $_.Extension -in @('.cs','.csproj','.props','.targets','.ps1','.mjs','.js','.json','.md','.aspx','.config','.runsettings') } |
            Sort-Object FullName | ForEach-Object { "$([IO.Path]::GetRelativePath($repoRoot,$_.FullName)) $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }
    }
    foreach ($file in @('global.json','Directory.Build.props','Directory.Build.targets','FxDbg.sln')) {
        "$file $((Get-FileHash -LiteralPath (Join-Path $repoRoot $file) -Algorithm SHA256).Hash)"
    }
}
$inputs = @(Get-ValidationInputs)
$inputs | Set-Content -LiteralPath (Join-Path $report 'stage3-inputs.sha256') -Encoding utf8
$outcomes = [Collections.Generic.List[object]]::new()
function Invoke-Validation([string]$Name, [string[]]$Arguments, [int]$Seconds) {
    $log = Join-Path $report "$Name.log"
    Write-Host "Running $Name (deadline ${Seconds}s)..."
    $code = (Invoke-ValidationProcess -Executable ($shell) -Arguments ($Arguments) -Directory ($repoRoot) -Log ($log) -Seconds ($Seconds))
    $outcomes.Add(@{ name = $Name; exitCode = $code; log = $log })
    if ($code -ne 0) { Get-Content -LiteralPath $log -Tail 70 | Write-Host; throw "$Name failed: $code ($log)" }
    Write-Host "$Name passed. Evidence: $log"
}
$configurationArguments = if ($Configuration) { @('-Configuration',$Configuration) } else { @() }
$passed = $false
$validationRun = Enter-ValidationRun
try {
    foreach ($architecture in @('x86','x64')) { $null = & (Join-Path $PSScriptRoot 'find-windows-debugger.ps1') -Architecture $architecture }
    Initialize-ValidationBuilds $validationRun $Configuration
    Invoke-Validation 'final-package' @('-NoProfile','-File',(Join-Path $PSScriptRoot 'package-extension.ps1')) 240
    # The multi-domain script includes both architectures of native lifecycle/event verification.
    Invoke-Validation 'final-appdomains' (@('-NoProfile','-File',(Join-Path $PSScriptRoot 'verify-stage3-3.ps1')) + $configurationArguments) 1200
    Invoke-Validation 'final-paging' (@('-NoProfile','-File',(Join-Path $PSScriptRoot 'verify-stage3-1.ps1')) + $configurationArguments) 1200
    Invoke-Validation 'final-source-mappings' (@('-NoProfile','-File',(Join-Path $PSScriptRoot 'verify-stage3-2.ps1')) + $configurationArguments) 1200
    Invoke-Validation 'final-dap' (@('-NoProfile','-File',(Join-Path $PSScriptRoot 'verify-stage3-5.ps1'),'-NodePath',$NodePath,'-CodePath',$CodePath) + $configurationArguments) 1500
    if(-not $SkipServiceIis) {
        Invoke-Validation 'final-service-iis' (@('-NoProfile','-File',(Join-Path $PSScriptRoot 'verify-stage3-4.ps1'),'-NodePath',$NodePath,'-CodePath',$CodePath) + $configurationArguments) 7200
    }
    Invoke-Validation 'final-stage2-regression' (@('-NoProfile','-File',(Join-Path $PSScriptRoot 'verify-stage2.ps1')) + $configurationArguments) 4200
    if (Compare-Object $inputs @(Get-ValidationInputs)) { throw 'Source/test/package inputs changed during full validation; rerun.' }
    Confirm-ValidationRun $validationRun
    $passed = $true
}
finally {
    Exit-ValidationRun $validationRun
    @{ passed = ($passed -and -not $SkipServiceIis); subsetPassed = $passed; configuration = $(if ($Configuration) { $Configuration } else { 'Debug+Release' }); skipped = @(if($SkipServiceIis){'3-4'});
        atUtc = [DateTimeOffset]::UtcNow.ToString('o'); outcomes = @($outcomes.ToArray()) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $report 'stage3-result.json') -Encoding utf8
}
if($SkipServiceIis) { Write-Host 'Requested subset passed; Service/IIS was explicitly skipped. This is not full stage 3 acceptance.' }
else { Write-Host 'Full stage 3 including Service/IIS, VS Code and complete stage 2/1/0 regressions passed.' }
