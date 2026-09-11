param([ValidateSet('Debug','Release')][string]$Configuration,[string]$NodePath='C:\Program Files\nodejs\node.exe')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'validation-reuse.ps1')
$repoRoot=Split-Path -Parent $PSScriptRoot
$NodePath=(Get-Command $NodePath -ErrorAction Stop).Source
if (-not ('FxDbg.Validation.ValidationProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ValidationProcess.cs') }
$shell=(Get-Process -Id $PID).Path
$ownedRun=Enter-ValidationRun
$runDirectory=Get-ValidationRun
$passed=$false
$outcomes=[Collections.Generic.List[object]]::new()
try {
    Initialize-ValidationBuilds $ownedRun $Configuration
    # These suites now include ADR-005 syntax, intrinsics, proved properties and
    # diagnostics in all four entry points, plus the real conditional-stop oracle.
    foreach($stage in @('4-1','4-3')) {
        $arguments=@('-NoProfile','-File',(Join-Path $PSScriptRoot "verify-stage$stage.ps1"),'-NodePath',$NodePath)
        if($Configuration) { $arguments+=@('-Configuration',$Configuration) }
        $log=Join-Path $runDirectory "expression-stage$stage.log"
        $code=Invoke-ValidationProcess $shell $arguments $repoRoot $log 1800
        $outcomes.Add(@{stage=$stage;exitCode=$code;log=$log})
        if($code -ne 0) { Get-Content -LiteralPath $log -Tail 40 | Write-Host; throw "Expression stage $stage failed ($code): $log" }
        $result=Get-Content -LiteralPath (Join-Path $repoRoot "artifacts/stage4-validation/stage$stage-result.json") -Raw | ConvertFrom-Json
        if(-not $result.passed) { throw "Expression stage $stage did not report success." }
    }
    Confirm-ValidationRun $ownedRun
    $passed=$true
} finally {
    @{passed=$passed;configuration=$(if($Configuration){$Configuration}else{'Debug+Release'});
        atUtc=[DateTimeOffset]::UtcNow.ToString('o');runDirectory=$runDirectory;outcomes=@($outcomes.ToArray())} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDirectory 'expression-extension-result.json')
    Exit-ValidationRun $ownedRun
}
Write-Host "Expression extension validation passed: $runDirectory"
