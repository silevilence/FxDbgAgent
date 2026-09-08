param([Parameter(Mandatory)][string]$RepoRoot)
$ErrorActionPreference='Stop'
$RepoRoot=[IO.Path]::GetFullPath($RepoRoot)
$mailbox=Join-Path $RepoRoot 'artifacts/stage4-preflight'
$runId='stage4-final-accepted'
$statusPath=Join-Path $mailbox "$runId.status.json"
$status=Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
if($status.status -ne 'completed' -or $status.exitCode -ne 0) { throw 'Final administrator run has not completed successfully.' }
$startRecord=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'admin-start.json') -Raw | ConvertFrom-Json
$started=[DateTimeOffset]::Parse($startRecord.startedUtc).UtcDateTime
$destination=Join-Path $PSScriptRoot 'regression'
$null=New-Item -ItemType Directory -Path $destination -Force
$records=[Collections.Generic.List[object]]::new()
function Archive([IO.FileInfo]$File) {
    if($File.LastWriteTimeUtc -lt $started) { throw "Stale evidence: $($File.FullName)" }
    $relative=[IO.Path]::GetRelativePath($RepoRoot,$File.FullName)
    if($relative.StartsWith('..') -or [IO.Path]::IsPathRooted($relative)) { throw 'Evidence must be in repository.' }
    $target=Join-Path $destination $relative
    $null=New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
    $originalHash=(Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash
    if($File.Extension -in @('.log','.processes')) {
        $lines=[IO.File]::ReadAllLines($File.FullName) | ForEach-Object { $_.TrimEnd() }
        [IO.File]::WriteAllText($target,($lines -join "`n").TrimEnd()+"`n",[Text.UTF8Encoding]::new($false))
    } else { Copy-Item -LiteralPath $File.FullName -Destination $target -Force }
    $records.Add(@{path=$relative;sourceModifiedUtc=$File.LastWriteTimeUtc.ToString('o');sourceSha256=$originalHash;archiveSha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash})
}
$results=@(
    'stage3-validation/stage3-result.json','stage3-4-validation/stage3-4-result.json',
    'stage3-4-validation/environment-result.json','stage3-4-validation/permissions-result.json',
    'stage3-4-validation/services-result.json','stage3-4-validation/iis-result.json',
    'stage3-4-validation/lifecycle-result.json','stage2-validation/stage2-result.json')
foreach($relative in $results) {
    $file=Get-Item -LiteralPath (Join-Path $RepoRoot "artifacts/$relative")
    if($file.LastWriteTimeUtc -lt $started) { throw "Stale result: $relative" }
    $result=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    if(-not $result.passed -or @($result.skipped).Where({$_}).Count) { throw "Failed or skipped result: $relative" }
    foreach($outcome in $result.outcomes) {
        $expected=if($null -ne $outcome.expected){$outcome.expected}else{0}
        if($outcome.exitCode -ne $expected) { throw "Unexpected outcome: $relative $($outcome.name)" }
    }
}
$inputs=Get-Content -LiteralPath (Join-Path $RepoRoot 'artifacts/stage3-validation/stage3-inputs.sha256')
$inputDifference=@(Compare-Object $inputs (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'stage4-matrix/stage4-3-inputs.sha256')))
$scopeDifference=$inputDifference
# Stage3 additionally hashes two IIS pages and four root build inputs; stage4's input function does not list them.
$extraScope='^(tests\\Debuggees\\Fx40\.Environment\.Web\\(health|late)\.aspx|global\.json|Directory\.Build\.props|Directory\.Build\.targets|FxDbg\.sln) '
if($scopeDifference.Count -ne 6 -or @($scopeDifference | Where-Object { $_.SideIndicator -ne '<=' -or $_.InputObject -notmatch $extraScope }).Count) { throw 'Unexpected input differences from stage4 matrix.' }
foreach($directory in @('stage3-validation','stage3-4-validation','stage2-validation','stage1-validation','stage1-12','stage1-4','stage1-5','stage1-6','stage1-9','stage0-4','stage0-5','stage0-6')) {
    $path=Join-Path $RepoRoot "artifacts/$directory"
    foreach($file in Get-ChildItem -LiteralPath $path -File | Where-Object { $_.LastWriteTimeUtc -ge $started -and $_.Extension -in @('.json','.sha256','.log','.processes') }) { Archive $file }
}
foreach($suffix in @('check','permissions','services','iis','lifecycle')) {
    $file=Get-Item -LiteralPath (Join-Path $RepoRoot "artifacts/stage3-4-environment-$suffix/state.json")
    $state=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    if($state.status -ne 'removed') { throw "Test resources not removed: $suffix" }
    Archive $file
}
foreach($name in @("$runId.status.json","$runId.log","$runId.log.processes")) { Archive (Get-Item -LiteralPath (Join-Path $mailbox $name)) }
$badProcesses=@(Get-ChildItem -LiteralPath $destination -Recurse -File -Filter '*.processes' | Select-String -Pattern 'exited=False')
$timeouts=@(Get-ChildItem -LiteralPath $destination -Recurse -File -Filter '*.log' | Select-String -Pattern 'timedOut=True|forcedOwnedCleanup=\S')
if($badProcesses.Count -or $timeouts.Count) { throw 'Inspect process cleanup or timeout evidence before acceptance.' }
@{passed=$true;reviewedCommit='2aa8a9d4067646fecf0707760980ca629312396a';startedUtc=$startRecord.startedUtc;endedUtc=$status.endedUtc;inputsCount=@($inputs).Count;sharedInputsIdenticalToStage4=$true;additionalInputScope=@($inputDifference);inputsSha256=(Get-FileHash -LiteralPath (Join-Path $RepoRoot 'artifacts/stage3-validation/stage3-inputs.sha256')).Hash;files=@($records.ToArray());normalization='Only log/process record trailing whitespace and final newline normalized; raw and archived hashes both recorded.'} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'manifest.json') -Encoding utf8
Write-Host "Archived $($records.Count) current-run evidence files; source inputs, results and cleanup passed."
