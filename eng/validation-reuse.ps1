# Shared preparation is scoped to one serial validation run, never to a previous run.
$validationRepoRoot = Split-Path -Parent $PSScriptRoot

function Get-ValidationFingerprint {
    $paths = foreach ($directory in @('src','tests','eng','extensions','skills')) {
        Get-ChildItem -LiteralPath (Join-Path $validationRepoRoot $directory) -Recurse -File |
            Where-Object { [IO.Path]::GetRelativePath($validationRepoRoot,$_.FullName) -notmatch '(^|[\\/])(bin|obj|node_modules|TestResults|artifacts|\.vs|\.git)[\\/]' }
    }
    $paths += Get-ChildItem -LiteralPath $validationRepoRoot -File
    $lines = $paths | Sort-Object FullName | ForEach-Object {
        "$($_.FullName) $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))))
}

function Enter-ValidationRun {
    if ($env:FXDBG_VALIDATION_RUN) { return $null }
    $directory = Join-Path $validationRepoRoot ('artifacts/validation-runs/' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $directory
    $owner = Get-Process -Id $PID
    @{ root=$validationRepoRoot; ownerId=$PID; ownerStartedTicks=$owner.StartTime.ToUniversalTime().Ticks;
        fingerprint=(Get-ValidationFingerprint); startedUtc=[DateTimeOffset]::UtcNow.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'run.json')
    $env:FXDBG_VALIDATION_RUN = $directory
    Write-Host "Shared validation preparation: $directory"
    return $directory
}

function Get-ValidationRun {
    if (-not $env:FXDBG_VALIDATION_RUN) { return $null }
    $directory = [IO.Path]::GetFullPath($env:FXDBG_VALIDATION_RUN)
    $allowed = [IO.Path]::GetFullPath((Join-Path $validationRepoRoot 'artifacts/validation-runs')) + [IO.Path]::DirectorySeparatorChar
    if (-not $directory.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid validation run directory.' }
    $run = Get-Content -LiteralPath (Join-Path $directory 'run.json') -Raw | ConvertFrom-Json
    $owner = Get-Process -Id $run.ownerId -ErrorAction Stop
    if ($run.root -ne $validationRepoRoot -or $owner.StartTime.ToUniversalTime().Ticks -ne $run.ownerStartedTicks -or
        (Test-Path -LiteralPath (Join-Path $directory 'closed.json'))) { throw 'Validation preparation belongs to a different or closed run.' }
    if ($run.fingerprint -ne (Get-ValidationFingerprint)) { throw 'Validation inputs changed; start a new full run.' }
    return $directory
}

function Exit-ValidationRun([string]$OwnedRun) {
    if ($OwnedRun) {
        try { @{closedUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OwnedRun 'closed.json') }
        finally { Remove-Item Env:FXDBG_VALIDATION_RUN -ErrorAction SilentlyContinue }
    }
}

function Confirm-ValidationRun([string]$OwnedRun) {
    $null = Get-ValidationRun
    if (-not $OwnedRun) { return }
    # One final byte-level audit catches changes even when length and timestamp were preserved.
    $hashes = @{}
    foreach ($file in Get-ChildItem -LiteralPath $OwnedRun -Filter '*.json') {
        $record = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if (-not $record.key) { continue }
        if (Compare-Object @($record.state) @(Get-ValidationOutputState $record.directories)) { throw "Prepared output changed: $($record.key)" }
        foreach ($expected in $record.hashes) {
            if (-not $hashes.ContainsKey($expected.path)) { $hashes[$expected.path] = (Get-FileHash -LiteralPath $expected.path -Algorithm SHA256).Hash }
            if ($hashes[$expected.path] -ne $expected.sha256) { throw "Prepared output hash changed: $($expected.path)" }
        }
    }
    @{verifiedUtc=[DateTimeOffset]::UtcNow.ToString('o');files=$hashes.Count;inputsUnchanged=$true} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OwnedRun 'verification.json')
}

function Initialize-ValidationBuilds([string]$OwnedRun,[string]$Configuration) {
    if (-not $OwnedRun) { return }
    foreach ($current in $(if($Configuration){@($Configuration)}else{@('Debug','Release')})) {
        Invoke-ValidationDotNet -Arguments @('build',(Join-Path $validationRepoRoot 'FxDbg.sln'),'-c',$current)
    }
}

function Get-ValidationOutputState([string[]]$Directories) {
    # Compare the complete file set and metadata on every reuse. Hashes are also saved for audit.
    foreach ($directory in $Directories) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw "Prepared output is missing: $directory" }
        Get-ChildItem -LiteralPath $directory -Recurse -File | Sort-Object FullName | ForEach-Object {
            "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)"
        }
    }
}

function Save-ValidationPreparation([string]$Key,[string[]]$Directories,[string]$Log) {
    $directory = Get-ValidationRun
    if (-not $directory) { return }
    $files = @(foreach ($path in $Directories) { Get-ChildItem -LiteralPath $path -Recurse -File })
    if ($files.Count -eq 0) { throw "No outputs for $Key" }
    @{ key=$Key; log=$Log; completedUtc=[DateTimeOffset]::UtcNow.ToString('o'); directories=$Directories;
        state=@(Get-ValidationOutputState $Directories);
        hashes=@($files | Sort-Object FullName | ForEach-Object { @{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} }) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $directory "$Key.json")
}

function Test-ValidationPreparation([string]$Key) {
    $directory = Get-ValidationRun
    if (-not $directory) { return $false }
    $path = Join-Path $directory "$Key.json"
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    $record = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if (Compare-Object @($record.state) @(Get-ValidationOutputState $record.directories)) {
        throw "Prepared output changed: $Key. Start a new full run; do not mix build or coverage artifacts."
    }
    Write-Host "REUSED $Key; original evidence: $($record.log); manifest: $path"
    @{key=$Key;atUtc=[DateTimeOffset]::UtcNow.ToString('o');manifest=$path;log=$record.log} |
        ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $directory 'reuse.jsonl')
    return $true
}

function Invoke-ValidationProcess([string]$Executable,[string[]]$Arguments,[string]$Directory,[string]$Log,[int]$Seconds) {
    if ([IO.Path]::GetFileNameWithoutExtension($Executable) -eq 'dotnet' -and $Arguments[0] -in @('build','test')) {
        $Executable = (Get-Process -Id $PID).Path
        $Arguments = @('-NoProfile','-File',(Join-Path $PSScriptRoot 'validation-dotnet.ps1')) + $Arguments
    }
    return [FxDbg.Validation.ValidationProcess]::Run($Executable,$Arguments,$Directory,$Log,$Seconds)
}

function Invoke-ValidationDotNet([string[]]$Arguments) {
    $directory = Get-ValidationRun
    $kind = $Arguments[0]
    $configuration = 'Debug'
    $supported = $kind -in @('build','test') -and $Arguments.Count -ge 2
    $project = if ($supported) { [IO.Path]::GetFullPath($Arguments[1],(Get-Location).Path) } else { '' }
    # Only the existing default build and ordinary unit-test options can share preparation.
    # Coverage, custom MSBuild properties, other projects and unknown arguments always execute.
    for ($i=2; $i -lt $Arguments.Count; $i++) {
        switch ($Arguments[$i]) {
            { $_ -in @('-c','--configuration') } {
                if ($i+1 -ge $Arguments.Count) { $supported=$false; break }
                $configuration=$Arguments[++$i]; break
            }
            '--filter' {
                $knownFilters = @('FullyQualifiedName~CliCommandTests','FullyQualifiedName~Breakpoints',
                    'FullyQualifiedName~PeArchitectureDetectorTests|FullyQualifiedName~EngineProcessHostTests',
                    'FullyQualifiedName~SingleThreadCommandSchedulerTests','FullyQualifiedName~Service_and_iis_fixtures')
                if ($kind -ne 'test' -or $i+1 -ge $Arguments.Count -or $Arguments[$i+1] -notin $knownFilters) { $supported=$false }
                $i++; break
            }
            { $_ -in @('--no-build','--no-restore','--no-incremental','--nologo') } { break }
            default { $supported=$false }
        }
    }
    $solution = Join-Path $validationRepoRoot 'FxDbg.sln'
    $members = @(Select-String -LiteralPath $solution -Pattern '^Project\(.*?\) = ".*?", "(.*?\.csproj)"' |
        ForEach-Object { [IO.Path]::GetFullPath($_.Matches[0].Groups[1].Value,$validationRepoRoot) })
    $unitProject = Join-Path $validationRepoRoot 'tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj'
    $coverageProject = Join-Path $validationRepoRoot 'tests/FxDbg.ServiceIisCoverageTests/FxDbg.ServiceIisCoverageTests.csproj'
    if ($directory -and $supported -and $kind -eq 'build' -and $project -eq $coverageProject -and $configuration -in @('Debug','Release')) {
        if (-not (Test-ValidationPreparation "coverage-build-$configuration")) {
            if (-not ('FxDbg.Validation.ValidationProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ValidationProcess.cs') }
            $log = Join-Path $directory "coverage-build-$configuration.log"
            $exitCode = [FxDbg.Validation.ValidationProcess]::Run('dotnet',$Arguments,$validationRepoRoot,$log,180)
            Get-Content -LiteralPath $log | Write-Host
            if ($exitCode -ne 0) { throw "Coverage harness build failed ($exitCode): $log" }
            Save-ValidationPreparation "coverage-build-$configuration" @((Join-Path (Split-Path -Parent $coverageProject) "bin/$configuration")) $log
        }
        $global:LASTEXITCODE = 0
        return
    }
    $supported = $supported -and $configuration -in @('Debug','Release') -and
        (($kind -eq 'build' -and ($project -eq $solution -or $project -in $members)) -or ($kind -eq 'test' -and $project -eq $unitProject))
    if (-not $directory -or -not $supported) {
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet $kind failed: $LASTEXITCODE" }
        $global:LASTEXITCODE = 0
        return
    }
    $env:DOTNET_CLI_HOME = Join-Path $validationRepoRoot 'artifacts/dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $validationRepoRoot 'artifacts/nuget-packages'
    if (-not ('FxDbg.Validation.ValidationProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ValidationProcess.cs') }
    if (-not (Test-ValidationPreparation "build-$configuration")) {
        $log = Join-Path $directory "build-$configuration.log"
        $exitCode = [FxDbg.Validation.ValidationProcess]::Run('dotnet',@('build',$solution,'-c',$configuration,'--no-incremental'),$validationRepoRoot,$log,600)
        Get-Content -LiteralPath $log | Write-Host
        if ($exitCode -ne 0) { throw "Shared build failed ($exitCode): $log" }
        $outputs = @($members | ForEach-Object { Join-Path (Split-Path -Parent $_) "bin/$configuration" } | Sort-Object -Unique)
        Save-ValidationPreparation "build-$configuration" $outputs $log
    }
    if ($kind -eq 'test' -and -not (Test-ValidationPreparation "unit-$configuration")) {
        $log = Join-Path $directory "unit-$configuration.log"
        $exitCode = [FxDbg.Validation.ValidationProcess]::Run('dotnet',@('test',$unitProject,'-c',$configuration,'--no-build','--no-restore'),$validationRepoRoot,$log,180)
        Get-Content -LiteralPath $log | Write-Host
        if ($exitCode -ne 0) { throw "Shared unit suite failed ($exitCode): $log" }
        Save-ValidationPreparation "unit-$configuration" @((Join-Path $validationRepoRoot "tests/FxDbg.UnitTests/bin/$configuration")) $log
    }
    $global:LASTEXITCODE = 0
}
