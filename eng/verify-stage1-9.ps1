param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'validation-reuse.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    & (Join-Path $PSScriptRoot 'validation-dotnet.ps1') build FxDbg.sln --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & (Join-Path $PSScriptRoot 'validation-dotnet.ps1') test tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj --configuration $Configuration --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
    foreach ($architecture in @('x86', 'x64')) {
        $runner = Join-Path $repoRoot "tests/FxDbg.IntegrationTests/bin/$Configuration/net48/FxDbg.IntegrationTests.$architecture.exe"
        foreach ($mode in @('modules', 'stack')) {
            & $runner $repoRoot $Configuration $mode
            if ($LASTEXITCODE -ne 0) { throw "Real $architecture $mode test failed." }
        }
        & $runner $repoRoot $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Real $architecture breakpoint regression failed." }
    }
    Write-Host 'Stage 1-9 verification passed.'
}
finally { Pop-Location }
