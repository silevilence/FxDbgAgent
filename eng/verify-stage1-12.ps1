param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $repoRoot 'artifacts/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot 'artifacts/nuget-packages'
Push-Location $repoRoot
try {
    dotnet build FxDbg.sln --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj --configuration $Configuration --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
    foreach ($architecture in @('x86','x64')) {
        $runner = Join-Path $repoRoot "tests/FxDbg.IntegrationTests/bin/$Configuration/net48/FxDbg.IntegrationTests.$architecture.exe"
        foreach ($mode in @('breakpoints','breakpoint-race','execution','variables','exceptions','modules')) {
            & $runner $repoRoot $Configuration $mode
            if ($LASTEXITCODE -ne 0) { throw "$architecture $mode regression failed." }
        }
    }
    & ./eng/verify-stage1-3.ps1 -Configuration $Configuration
    & ./eng/verify-stage1-6.ps1 -Configuration $Configuration
    $hostRunner = Join-Path $repoRoot "tests/FxDbg.HostIntegrationTests/bin/$Configuration/net10.0-windows/FxDbg.HostIntegrationTests.dll"
    dotnet $hostRunner $repoRoot $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Protocol/lifecycle regression failed.' }
    dotnet $hostRunner $repoRoot $Configuration e2e
    if ($LASTEXITCODE -ne 0) { throw 'Console/WinForms end-to-end matrix failed.' }
    dotnet $hostRunner $repoRoot $Configuration review
    if ($LASTEXITCODE -ne 0) { throw 'Runtime version or debugger conflict regression failed.' }
    & ./eng/verify-stage1-11.ps1 -Configuration $Configuration
    & ./eng/verify-stage0-3.ps1 -Configuration $Configuration
    & ./eng/verify-stage0-4.ps1 -Configuration $Configuration
    & ./eng/verify-stage0-6.ps1 -Configuration $Configuration
    Write-Host "Stage 1-12 full $Configuration verification passed."
}
finally { Pop-Location }
