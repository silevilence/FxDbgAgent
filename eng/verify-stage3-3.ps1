param([ValidateSet('Debug','Release')][string]$Configuration)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'verify-stage3-feature.ps1') @PSBoundParameters -Suites appdomains
$repoRoot = Split-Path -Parent $PSScriptRoot
$configurations = if ($Configuration) { @($Configuration) } else { @('Debug','Release') }
foreach ($current in $configurations) {
    foreach ($architecture in @('x86','x64')) {
        $code = [FxDbg.Validation.ValidationProcess]::Run(
            (Join-Path $repoRoot "tests/FxDbg.IntegrationTests/bin/$current/net48/FxDbg.IntegrationTests.$architecture.exe"),
            @($repoRoot,$current),$repoRoot,(Join-Path $repoRoot "artifacts/stage3-validation/domains-events-$current-$architecture.log"),120)
        if ($code -ne 0) { throw "AppDomain events failed: $current $architecture ($code)" }
    }
}
