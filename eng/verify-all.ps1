param([ValidateSet('Debug','Release')][string]$Configuration,[string]$NodePath='node',[string]$CodePath='code')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'validation-reuse.ps1')
$repoRoot=Split-Path -Parent $PSScriptRoot
if (-not [Environment]::Is64BitProcess -or -not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Full validation requires elevated 64-bit PowerShell for real Service/IIS acceptance.'
}
$NodePath=(Get-Command $NodePath -ErrorAction Stop).Source
$CodePath=& (Join-Path $PSScriptRoot 'find-vscode.ps1') -CodePath $CodePath
foreach($architecture in @('x86','x64')) { $null=& (Join-Path $PSScriptRoot 'find-windows-debugger.ps1') -Architecture $architecture }
if (-not ('FxDbg.Validation.ValidationProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ValidationProcess.cs') }
$shell=(Get-Process -Id $PID).Path
$validationRun=Enter-ValidationRun
$runDirectory=Get-ValidationRun
$passed=$false
try {
    Initialize-ValidationBuilds $validationRun $Configuration
    # Stage 4 and the complete stage 3/2/1/0 matrix share one frozen preparation.
    # Every native/MCP/DAP/CLI/VS Code scenario still runs; no Service/IIS skip option.
    foreach($stage in @('4-1','4-2','4-3','3')) {
        $arguments=@('-NoProfile','-File',(Join-Path $PSScriptRoot "verify-stage$stage.ps1"),'-NodePath',$NodePath)
        if($Configuration) { $arguments+=@('-Configuration',$Configuration) }
        if($stage -eq '3') { $arguments+=@('-CodePath',$CodePath) }
        $log=Join-Path $runDirectory "stage$stage.log"
        $seconds=if($stage -eq '3'){14400}else{1800}
        Write-Host "Running stage $stage; evidence: $log"
        $code=Invoke-ValidationProcess $shell $arguments $repoRoot $log $seconds
        if($code -ne 0) { Get-Content -LiteralPath $log -Tail 40 | Write-Host; throw "Stage $stage failed ($code): $log" }
    }
    Confirm-ValidationRun $validationRun
    $passed=$true
} finally {
    @{passed=$passed;configuration=$(if($Configuration){$Configuration}else{'Debug+Release'});
        atUtc=[DateTimeOffset]::UtcNow.ToString('o');runDirectory=$runDirectory} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'result.json')
    Exit-ValidationRun $validationRun
}
Write-Host "Full stage 4/3/2/1/0 validation passed: $runDirectory"
