param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    dotnet build FxDbg.sln --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj --configuration $Configuration --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
    $runner = Join-Path $repoRoot "tests/FxDbg.HostIntegrationTests/bin/$Configuration/net10.0-windows/FxDbg.HostIntegrationTests.dll"
    dotnet $runner $repoRoot $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Host/Engine protocol and lifecycle integration failed.' }
    Write-Host 'Stage 1-10 verification passed.'
}
finally { Pop-Location }
