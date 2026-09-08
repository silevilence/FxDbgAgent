param([Parameter(Mandatory)][ValidateSet('permissions','iis')][string]$Suite,[ValidateSet('Debug','Release')][string]$Configuration='Debug')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME=Join-Path $repoRoot 'artifacts/dotnet-home'
$env:NUGET_PACKAGES=Join-Path $repoRoot 'artifacts/nuget-packages'
$env:FXDBG_COVERAGE_ROOT=$repoRoot
$env:FXDBG_COVERAGE_CONFIGURATION=$Configuration
$env:FXDBG_COVERAGE_SUITE=$Suite
$results=Join-Path $repoRoot "artifacts/stage3-4-validation/coverage-$Suite-$Configuration/$([Guid]::NewGuid().ToString('N'))"
& (Join-Path $PSScriptRoot 'validation-dotnet.ps1') build (Join-Path $repoRoot 'tests/FxDbg.ServiceIisCoverageTests/FxDbg.ServiceIisCoverageTests.csproj') -c $Configuration
dotnet test (Join-Path $repoRoot 'tests/FxDbg.ServiceIisCoverageTests/FxDbg.ServiceIisCoverageTests.csproj') -c $Configuration `
    --no-build --no-restore --settings (Join-Path $PSScriptRoot 'coverage.runsettings') --collect 'Code Coverage;Format=cobertura' --results-directory $results
if($LASTEXITCODE -ne 0) { throw "Measured real $Suite matrix failed." }
$files=@(Get-ChildItem -LiteralPath $results -Recurse -Filter '*.cobertura.xml')
if($files.Count -eq 0) { throw 'The collector produced no Cobertura evidence.' }
$packages=@(foreach($file in $files) { ([xml](Get-Content -LiteralPath $file.FullName -Raw)).coverage.packages.package.name })
foreach($required in @('FxDbg.Host','FxDbg.Engine.x86','FxDbg.Engine.x64','FxDbg.Interop')) {
    if($required -notin $packages) { throw "Coverage omitted child process assembly: $required" }
}
