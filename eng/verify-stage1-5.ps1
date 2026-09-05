param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    dotnet build FxDbg.sln --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj --configuration $Configuration --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
    foreach ($architecture in @('x86', 'x64')) {
        $runner = Join-Path $repoRoot "tests/FxDbg.IntegrationTests/bin/$Configuration/net48/FxDbg.IntegrationTests.$architecture.exe"
        & $runner $repoRoot $Configuration execution
        if ($LASTEXITCODE -ne 0) { throw "Real $architecture execution control test failed." }
        & $runner $repoRoot $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Real $architecture breakpoint regression failed." }
    }
    Write-Host 'Stage 1-5 verification passed.'
}
finally { Pop-Location }
