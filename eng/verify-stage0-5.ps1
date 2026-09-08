[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'validation-reuse.ps1')
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$env:DOTNET_CLI_HOME = Join-Path $artifactsRoot 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $artifactsRoot 'nuget-packages'
$reportDirectory = Join-Path $artifactsRoot 'stage0-5'

function Invoke-SymbolProbe {
    param(
        [string]$ProbePath,
        [string]$AssemblyPath,
        [string]$PdbPath,
        [string]$SourcePath,
        [int]$Line
    )

    $output = & $ProbePath map --assembly $AssemblyPath --pdb $PdbPath --source $SourcePath --line $Line
    if ($LASTEXITCODE -ne 0) {
        throw "Symbol probe failed with exit code $LASTEXITCODE."
    }

    return ($output | ConvertFrom-Json)
}

Push-Location $repositoryRoot
try {
    & ./eng/verify-stage0-1.ps1 -Configuration $Configuration
    & (Join-Path $PSScriptRoot 'validation-dotnet.ps1') build 'tests/Probes/FxDbg.Symbols.Probe/FxDbg.Symbols.Probe.csproj' --configuration $Configuration --no-incremental
    if ($LASTEXITCODE -ne 0) {
        throw "Symbol probe build failed with exit code $LASTEXITCODE."
    }

    [void](New-Item -ItemType Directory -Force -Path $reportDirectory)
    $probePath = Join-Path $repositoryRoot "tests/Probes/FxDbg.Symbols.Probe/bin/$Configuration/net8.0-windows/FxDbg.Symbols.Probe.exe"
    $assemblyPath = Join-Path $repositoryRoot "tests/Debuggees/Fx40.Console.x86/bin/$Configuration/net40/Fx40.Console.x86.exe"
    $pdbPath = Join-Path $repositoryRoot "tests/Debuggees/Fx40.Console.x86/bin/$Configuration/net40/Fx40.Console.x86.pdb"
    $sourcePath = Join-Path $repositoryRoot 'tests/Debuggees/Fx40.Console.x86/Program.cs'
    $sourceLine = (Select-String -LiteralPath $sourcePath -SimpleMatch 'Console.WriteLine("FxDbg .NET Framework 4.0 x86 debuggee")').LineNumber
    if ($null -eq $sourceLine) {
        throw 'Could not locate the known source line used by the symbol test.'
    }

    $loaded = Invoke-SymbolProbe -ProbePath $probePath -AssemblyPath $assemblyPath -PdbPath $pdbPath -SourcePath $sourcePath -Line $sourceLine
    if ($loaded.status -ne 'loaded' -or $loaded.pdbFormat -ne 'windows') {
        throw "Expected a loaded Windows PDB: $($loaded | ConvertTo-Json -Compress -Depth 5)"
    }
    $mainMapping = @($loaded.mappings | Where-Object { $_.methodName -eq 'FxDbg.Debuggees.ConsoleX86.Program.Main' })
    if ($mainMapping.Count -ne 1 -or $mainMapping[0].ilOffsets.Count -eq 0) {
        throw "Known source line did not map to Program.Main and an IL offset: $($loaded | ConvertTo-Json -Compress -Depth 5)"
    }

    $withoutMapping = Invoke-SymbolProbe -ProbePath $probePath -AssemblyPath $assemblyPath -PdbPath $pdbPath -SourcePath $sourcePath -Line 1
    if ($withoutMapping.status -ne 'loaded' -or @($withoutMapping.mappings).Count -ne 0) {
        throw "A valid PDB with no sequence point at the requested line must stay loaded: $($withoutMapping | ConvertTo-Json -Compress -Depth 5)"
    }

    $missingPdbPath = Join-Path $reportDirectory 'does-not-exist.pdb'
    $missing = Invoke-SymbolProbe -ProbePath $probePath -AssemblyPath $assemblyPath -PdbPath $missingPdbPath -SourcePath $sourcePath -Line $sourceLine
    if ($missing.status -ne 'pdb_missing') {
        throw "Expected pdb_missing: $($missing | ConvertTo-Json -Compress -Depth 5)"
    }

    $mismatchedPdbPath = Join-Path $repositoryRoot "tests/Debuggees/Fx40.Console.x64/bin/$Configuration/net40/Fx40.Console.x64.pdb"
    $mismatch = Invoke-SymbolProbe -ProbePath $probePath -AssemblyPath $assemblyPath -PdbPath $mismatchedPdbPath -SourcePath $sourcePath -Line $sourceLine
    if ($mismatch.status -ne 'pdb_mismatch') {
        throw "Expected pdb_mismatch: $($mismatch | ConvertTo-Json -Compress -Depth 5)"
    }

    $malformedPdbPath = Join-Path $reportDirectory 'malformed.pdb'
    $malformedBytes = [System.Text.Encoding]::ASCII.GetBytes("Microsoft C/C++ MSF 7.00`r`nmalformed")
    [System.IO.File]::WriteAllBytes($malformedPdbPath, $malformedBytes)
    $failed = Invoke-SymbolProbe -ProbePath $probePath -AssemblyPath $assemblyPath -PdbPath $malformedPdbPath -SourcePath $sourcePath -Line $sourceLine
    if ($failed.status -ne 'read_failed') {
        throw "Expected read_failed: $($failed | ConvertTo-Json -Compress -Depth 5)"
    }
    if ($failed.errorCode -ne 'malformed_or_unsupported_pdb' -or [string]::IsNullOrWhiteSpace($failed.hResult)) {
        throw "Expected a classified native reader failure: $($failed | ConvertTo-Json -Compress -Depth 5)"
    }

    $loaded | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportDirectory 'pdb-loaded.json') -Encoding utf8
    $missing | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportDirectory 'pdb-missing.json') -Encoding utf8
    $mismatch | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportDirectory 'pdb-mismatch.json') -Encoding utf8
    $failed | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportDirectory 'pdb-read-failed.json') -Encoding utf8

    Write-Host "Stage 0-5 verification passed: line $sourceLine mapped to Program.Main, and all three symbol failure states were distinguished."
}
finally {
    Pop-Location
}
