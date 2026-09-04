param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Engine {
    param(
        [Parameter(Mandatory)] [string]$Engine,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    $lines = & $Engine @Arguments 2>&1
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = (($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()
    }
}

function Start-WaitingTarget {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 700
    return $process
}

function Assert-Rejection {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$Engine,
        [Parameter(Mandatory)] [System.Diagnostics.Process]$Target,
        [Parameter(Mandatory)] [string]$Architecture,
        [Parameter(Mandatory)] [int]$ExitCode,
        [Parameter(Mandatory)] [string]$ErrorCode
    )

    $result = Invoke-Engine -Engine $Engine -Arguments @(
        'attach', '--pid', $Target.Id, '--arch', $Architecture, '--timeout-ms', '10000')
    if ($result.ExitCode -ne $ExitCode -or $result.Output -notmatch ('"code":"' + $ErrorCode + '"')) {
        throw "$Name rejection differed: exit=$($result.ExitCode), output=$($result.Output)"
    }

    if ($Target.HasExited) {
        throw "$Name rejection terminated the target process."
    }
}

Push-Location $repoRoot
try {
    dotnet build FxDbg.sln --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }

    dotnet test tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj `
        --configuration $Configuration `
        --no-build `
        --filter 'FullyQualifiedName~PeArchitectureDetectorTests|FullyQualifiedName~EngineProcessHostTests'
    if ($LASTEXITCODE -ne 0) { throw 'Architecture tests failed.' }

    $engineRoot = Join-Path $repoRoot "src/FxDbg.Engine/bin/$Configuration/net48"
    $engineX86 = Join-Path $engineRoot 'FxDbg.Engine.x86.exe'
    $engineX64 = Join-Path $engineRoot 'FxDbg.Engine.x64.exe'
    $fx40X86 = Join-Path $repoRoot "tests/Debuggees/Fx40.Console.x86/bin/$Configuration/net40/Fx40.Console.x86.exe"
    $fx40X64 = Join-Path $repoRoot "tests/Debuggees/Fx40.Console.x64/bin/$Configuration/net40/Fx40.Console.x64.exe"
    $fx35X86 = Join-Path $repoRoot "tests/Debuggees/Fx35.Console.x86/bin/$Configuration/net35/Fx35.Console.x86.exe"
    $coreX64 = Join-Path $repoRoot "tests/Debuggees/Core80.Console.x64/bin/$Configuration/net8.0/Core80.Console.x64.exe"

    foreach ($case in @(
        @{ Engine=$engineX86; Target=$fx40X86; Arch='x86' },
        @{ Engine=$engineX64; Target=$fx40X64; Arch='x64' }
    )) {
        $launch = Invoke-Engine -Engine $case.Engine -Arguments @(
            'launch', '--exe', $case.Target, '--arch', $case.Arch,
            '--arg', '--probe', '--timeout-ms', '10000')
        if ($launch.ExitCode -ne 0) { throw "Launch $($case.Arch) failed: $($launch.Output)" }
        $launchResult = $launch.Output | ConvertFrom-Json
        if (-not $launchResult.ok -or $launchResult.architecture -ne $case.Arch -or
            $launchResult.runtimeVersion -ne 'v4.0.30319' -or $launchResult.sessionState -ne 'running') {
            throw "Launch $($case.Arch) result differed: $($launch.Output)"
        }

        $target = Start-WaitingTarget -Path $case.Target -Arguments @('--wait-milliseconds', '30000')
        try {
            $attach = Invoke-Engine -Engine $case.Engine -Arguments @(
                'attach', '--pid', $target.Id, '--arch', $case.Arch, '--timeout-ms', '10000')
            if ($attach.ExitCode -ne 0) { throw "Attach $($case.Arch) failed: $($attach.Output)" }
            $attachResult = $attach.Output | ConvertFrom-Json
            if (-not $attachResult.ok -or $attachResult.processId -ne $target.Id -or
                $attachResult.sessionState -ne 'running' -or $target.HasExited) {
                throw "Attach $($case.Arch) result differed: $($attach.Output)"
            }
        }
        finally {
            if (-not $target.HasExited) { Stop-Process -Id $target.Id -Force }
            $target.Dispose()
        }
    }

    $stopAtEntry = Invoke-Engine -Engine $engineX86 -Arguments @(
        'launch', '--exe', $fx40X86, '--arch', 'x86', '--stop-at-entry',
        '--arg', '--probe', '--timeout-ms', '10000')
    if ($stopAtEntry.ExitCode -ne 0 -or
        ($stopAtEntry.Output | ConvertFrom-Json).sessionState -ne 'stopped') {
        throw "Stop-at-entry result differed: exit=$($stopAtEntry.ExitCode), output=$($stopAtEntry.Output)"
    }

    foreach ($invalid in @(
        @('attach', '--pid', 'not-a-pid', '--arch', 'x86'),
        @('attach', '--pid', '1', '--arch', 'x86', '--timeout-ms', 'zero'),
        @('attach', '--pid', '1', '--arch', 'x86', '--session-id', 'not-a-uuid')
    )) {
        $result = Invoke-Engine -Engine $engineX86 -Arguments $invalid
        if ($result.ExitCode -ne 2 -or $result.Output -notmatch '"code":"invalid_request"') {
            throw "Invalid option mapping differed: exit=$($result.ExitCode), output=$($result.Output)"
        }
    }

    $observation = [System.IO.Path]::GetTempFileName()
    $workingDirectory = [System.IO.Path]::GetTempPath().TrimEnd('\')
    try {
        $launchInputs = Invoke-Engine -Engine $engineX86 -Arguments @(
            'launch', '--exe', $fx40X86, '--arch', 'x86',
            '--working-directory', $workingDirectory,
            '--env', 'FXDBG_TEST=environment-ok',
            '--arg', '--launch-observation', '--arg', $observation,
            '--arg', 'argument with spaces', '--timeout-ms', '10000')
        if ($launchInputs.ExitCode -ne 0) { throw "Launch input verification failed: $($launchInputs.Output)" }
        $expected = 'environment-ok|' + $workingDirectory + '|argument with spaces'
        $actual = Get-Content -LiteralPath $observation -Raw
        if ($actual -ne $expected) { throw "Launch inputs differed: expected=$expected actual=$actual" }
    }
    finally {
        Remove-Item -LiteralPath $observation -Force -ErrorAction SilentlyContinue
    }

    $rejections = @(
        @{ Name='architecture mismatch'; Engine=$engineX86; Target=$fx40X64; Args=@('--wait-milliseconds','30000'); Arch='x86'; Exit=10; Code='architecture_mismatch' },
        @{ Name='reverse architecture mismatch'; Engine=$engineX64; Target=$fx40X86; Args=@('--wait-milliseconds','30000'); Arch='x64'; Exit=10; Code='architecture_mismatch' },
        @{ Name='CoreCLR'; Engine=$engineX64; Target=$coreX64; Args=@('--wait-milliseconds','30000'); Arch='x64'; Exit=12; Code='core_clr_not_supported' },
        @{ Name='CLR v2'; Engine=$engineX86; Target=$fx35X86; Args=@('--wait-milliseconds','30000'); Arch='x86'; Exit=13; Code='unsupported_clr_version' },
        @{ Name='native'; Engine=$engineX64; Target=(Join-Path $env:WINDIR 'System32/ping.exe'); Args=@('-t','127.0.0.1'); Arch='x64'; Exit=11; Code='not_managed_process' }
    )
    foreach ($case in $rejections) {
        $target = Start-WaitingTarget -Path $case.Target -Arguments $case.Args
        try {
            Assert-Rejection -Name $case.Name -Engine $case.Engine -Target $target `
                -Architecture $case.Arch -ExitCode $case.Exit -ErrorCode $case.Code
        }
        finally {
            if (-not $target.HasExited) { Stop-Process -Id $target.Id -Force }
            $target.Dispose()
        }
    }

    Write-Host 'Stage 1-2 verification passed: product Engines covered launch/attach/auto x86+x64, stop-at-entry, and actionable rejection paths.'
}
finally {
    Pop-Location
}
