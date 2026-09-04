param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repoRoot
try {
    & (Join-Path $PSScriptRoot 'verify-stage1-2.ps1') -Configuration $Configuration

    dotnet test tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj `
        --configuration $Configuration `
        --no-build `
        --filter 'FullyQualifiedName~SingleThreadCommandSchedulerTests'
    if ($LASTEXITCODE -ne 0) { throw 'Command scheduler tests failed.' }

    $engineRoot = Join-Path $repoRoot "src/FxDbg.Engine/bin/$Configuration/net48"
    foreach ($case in @(
        @{
            Engine = Join-Path $engineRoot 'FxDbg.Engine.x86.exe'
            Target = Join-Path $repoRoot "tests/Debuggees/Fx40.Console.x86/bin/$Configuration/net40/Fx40.Console.x86.exe"
            Architecture = 'x86'
        },
        @{
            Engine = Join-Path $engineRoot 'FxDbg.Engine.x64.exe'
            Target = Join-Path $repoRoot "tests/Debuggees/Fx40.Console.x64/bin/$Configuration/net40/Fx40.Console.x64.exe"
            Architecture = 'x64'
        }
    )) {
        $lines = & $case.Engine launch --exe $case.Target --arch $case.Architecture `
            --arg --wait-milliseconds --arg 5000 --timeout-ms 10000 --verification-cycles 100 2>&1
        $engineExitCode = $LASTEXITCODE
        $output = (($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()
        if ($engineExitCode -ne 0) {
            throw "Real $($case.Architecture) scheduler cycle verification failed: $output"
        }

        $result = $output | ConvertFrom-Json
        try {
            if ($result.verificationStopCount -ne 100 -or
                $result.verificationContinueCount -ne 100 -or
                $result.verificationOutstandingStopCount -ne 0) {
                throw "Real $($case.Architecture) Continue pairing differed: $output"
            }
        }
        finally {
            Stop-Process -Id $result.processId -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host 'Stage 1-3 verification passed: callback handoff, single-thread serialization, 100 Continue pairs, timeout, and cancellation covered.'
}
finally {
    Pop-Location
}
