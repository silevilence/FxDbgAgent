param([ValidateSet('Debug','Release')][string]$Configuration, [string]$NodePath='node', [string]$CodePath='code')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'validation-reuse.ps1')
$repoRoot=Split-Path -Parent $PSScriptRoot
$report=Join-Path $repoRoot 'artifacts/stage3-4-validation'
$null=New-Item -ItemType Directory -Path $report -Force
if(-not ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Full Service/IIS acceptance requires elevated 64-bit PowerShell.' }
if(-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell for full IIS acceptance.' }
if(-not ('FxDbg.Validation.ValidationProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ValidationProcess.cs') }
$shell=(Get-Process -Id $PID).Path
$CodePath=& (Join-Path $PSScriptRoot 'find-vscode.ps1') -CodePath $CodePath
function Get-Inputs {
    foreach($directory in @('src','tests','eng','extensions','skills')) {
        Get-ChildItem -LiteralPath (Join-Path $repoRoot $directory) -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' -and $_.Extension -in @('.cs','.csproj','.props','.targets','.ps1','.mjs','.js','.json','.md','.aspx','.config','.runsettings') } |
            Sort-Object FullName | ForEach-Object { "$([IO.Path]::GetRelativePath($repoRoot,$_.FullName)) $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }
    }
    foreach($file in @('global.json','Directory.Build.props','Directory.Build.targets','FxDbg.sln')) { "$file $((Get-FileHash -LiteralPath (Join-Path $repoRoot $file) -Algorithm SHA256).Hash)" }
}
$inputs=@(Get-Inputs)
$inputs | Set-Content -LiteralPath (Join-Path $report 'stage3-4-inputs.sha256') -Encoding utf8
$outcomes=[Collections.Generic.List[object]]::new()
$passed=$false
$validationRun = Enter-ValidationRun
try {
    Initialize-ValidationBuilds $validationRun $Configuration
    foreach($task in @('a','b','c','d','e')) {
        $arguments=@('-NoProfile','-File',(Join-Path $PSScriptRoot "verify-stage3-4$task.ps1"))
        if($Configuration) { $arguments+=@('-Configuration',$Configuration) }
        if($task -eq 'c') { $arguments+=@('-NodePath',$NodePath,'-CodePath',$CodePath) }
        if($task -in @('b','d')) { $arguments+='-CollectCoverage' }
        $log=Join-Path $report "stage3-4$task.log"
        Write-Host "Running stage3-4$task..."
        $exit=(Invoke-ValidationProcess -Executable ($shell) -Arguments ($arguments) -Directory ($repoRoot) -Log ($log) -Seconds (1800))
        $outcomes.Add(@{task="3-4$task";exitCode=$exit;log=$log})
        if($exit -ne 0) { throw "Stage3-4$task failed ($exit): $log" }
    }
    if(Compare-Object $inputs @(Get-Inputs)) { throw 'Inputs changed during Service/IIS validation; rerun.' }
    Confirm-ValidationRun $validationRun
    $passed=$true
}
finally {
    Exit-ValidationRun $validationRun
    @{passed=$passed;configuration=$(if($Configuration){$Configuration}else{'Debug+Release'});skipped=@();
        atUtc=[DateTimeOffset]::UtcNow.ToString('o');identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name;
        os=[Environment]::OSVersion.VersionString;outcomes=@($outcomes.ToArray())} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $report 'stage3-4-result.json') -Encoding utf8
}
Write-Host 'Full Service/IIS matrix, CLI/DAP/VS Code and resource restoration passed.'
