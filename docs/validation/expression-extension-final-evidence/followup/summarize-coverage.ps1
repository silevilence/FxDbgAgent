param([Parameter(Mandatory)][string]$RepoRoot)
$ErrorActionPreference='Stop'
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'coverage-inputs.json') -Raw | ConvertFrom-Json
$coverageFiles=@(foreach($record in $manifest.inputs) {
    $file=Get-Item -LiteralPath (Join-Path $RepoRoot $record.path)
    if((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $record.sha256) { throw "Coverage input hash differs: $($record.path)" }
    $file
})
$lineHits=@{}
$methods=@{}
foreach($coverageFile in $coverageFiles) {
[xml]$coverage=Get-Content -LiteralPath $coverageFile.FullName -Raw
foreach($class in $coverage.coverage.packages.package.classes.class) {
    $file=[IO.Path]::GetRelativePath($manifest.sourceRoot,[string]$class.filename).Replace('\','/')
    if(-not $file.StartsWith('src/')) { continue }
    if(-not $lineHits.ContainsKey($file)) { $lineHits[$file]=@{} }
    foreach($line in $class.lines.line) {
        $number=[int]$line.number
        $lineHits[$file][$number]=[Math]::Max([int]$lineHits[$file][$number],[int]$line.hits)
    }
    foreach($method in $class.methods.method) {
        $key=$file+'|'+$class.name+'.'+$method.name+'|'+$method.signature
        if(-not $methods.ContainsKey($key)) { $methods[$key]=@{file=$file;name=$class.name+'.'+$method.name;lines=[Collections.Generic.HashSet[int]]::new()} }
        foreach($line in $method.lines.line) { $null=$methods[$key].lines.Add([int]$line.number) }
    }
}
}
$changed=@{}; $current=''
$safeDirectory=$RepoRoot.Replace('\','/')
$diff=git -C $RepoRoot -c "safe.directory=$safeDirectory" diff "$($manifest.baseline):src" $manifest.sourceTree --unified=0
if($LASTEXITCODE -ne 0) { throw 'Cannot read the fixed review diff.' }
foreach($text in $diff) {
    if($text -match '^\+\+\+ b/(.+)$') { $current='src/'+$Matches[1];$changed[$current]=[Collections.Generic.HashSet[int]]::new() }
    elseif($text -match '^@@.* \+(\d+)(?:,(\d+))? @@') {
        $first=[int]$Matches[1]; $count=if($Matches[2]){[int]$Matches[2]}else{1}
        for($index=0;$index -lt $count;$index++) { $null=$changed[$current].Add($first+$index) }
    }
}
$rows=@();$total=0;$covered=0
foreach($file in $changed.Keys | Sort-Object) {
    if(-not $file.EndsWith('.cs')) { continue }
    $measured=@($changed[$file] | Where-Object {$lineHits.ContainsKey($file) -and $lineHits[$file].ContainsKey($_)})
    $missing=@($measured | Where-Object {$lineHits[$file][$_] -eq 0} | Sort-Object)
    $total+=$measured.Count; $covered+=$measured.Count-$missing.Count
    $rows+=@{file=$file;executableChangedLines=$measured.Count;covered=$measured.Count-$missing.Count;uncovered=$missing;instrumented=$lineHits.ContainsKey($file)}
}
$allTotal=0;$allCovered=0
foreach($file in $lineHits.Keys) { $allTotal+=$lineHits[$file].Count; $allCovered+=@($lineHits[$file].Values | Where-Object {$_ -gt 0}).Count }
$incompleteMethods=@(foreach($method in $methods.Values) {
    if(-not $changed.ContainsKey($method.file)) { continue }
    $changedMethodLines=@($method.lines | Where-Object {$changed[$method.file].Contains($_)})
    if($changedMethodLines.Count -eq 0) { continue }
    $uncoveredChanged=@($changedMethodLines | Where-Object {$lineHits[$method.file][$_] -eq 0} | Sort-Object)
    if($uncoveredChanged.Count -eq 0) { continue }
    $methodCovered=@($method.lines | Where-Object {$lineHits[$method.file][$_] -gt 0}).Count
    @{file=$method.file;name=$method.name;covered=$methodCovered;total=$method.lines.Count;rate=$methodCovered/$method.lines.Count;uncoveredChangedLines=$uncoveredChanged}
})
$allIncompleteMethods=@(foreach($method in $methods.Values) {
    if($method.lines.Count -eq 0) { continue }
    $methodCovered=@($method.lines | Where-Object {$lineHits[$method.file][$_] -gt 0}).Count
    if($methodCovered -eq $method.lines.Count) { continue }
    @{file=$method.file;name=$method.name;covered=$methodCovered;total=$method.lines.Count;rate=$methodCovered/$method.lines.Count}
})
@{coverageFiles=@($coverageFiles.FullName);covered=$covered;total=$total;rate=$covered/$total;files=$rows;incompleteChangedMethods=$incompleteMethods;incompleteProductionMethods=$allIncompleteMethods;overallProduction=@{covered=$allCovered;total=$allTotal;rate=$allCovered/$allTotal}} | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $repoRoot 'artifacts/expression-review-followup/coverage-changes.json')
if (@($rows | Where-Object { -not $_.instrumented }).Count) { throw 'Changed production files lack instrumentation.' }
if ($total -eq 0 -or $covered/$total -lt 0.90) { throw "Changed production coverage is below 90%: $covered/$total" }
$rows | ConvertTo-Json -Depth 4
"Changed production coverage: $covered/$total = $([Math]::Round(100*$covered/$total,2))%"
