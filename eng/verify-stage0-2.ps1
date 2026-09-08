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

function Invoke-Probe {
    param(
        [string]$ProbePath,
        [string[]]$Arguments
    )

    $output = & $ProbePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "ClrDebug probe failed with exit code ${LASTEXITCODE}: $output"
    }

    return ($output | ConvertFrom-Json)
}

function Assert-ProbeResult {
    param(
        [object]$Result,
        [string]$ExpectedMode,
        [Nullable[int]]$ExpectedProcessId
    )

    if ($Result.mode -ne $ExpectedMode -or $Result.callback -ne 'CreateProcess') {
        throw "$ExpectedMode did not produce the expected CreateProcess callback: $($Result | ConvertTo-Json -Compress)"
    }
    if ($Result.runtimeVersion -ne 'v4.0.30319') {
        throw "$ExpectedMode selected unexpected CLR version '$($Result.runtimeVersion)'."
    }
    if ($Result.debuggerArchitecture -ne 'x86') {
        throw "$ExpectedMode probe ran as '$($Result.debuggerArchitecture)' instead of x86."
    }
    if ($Result.processId -le 0 -or ($null -ne $ExpectedProcessId -and $Result.processId -ne $ExpectedProcessId)) {
        throw "$ExpectedMode reported unexpected process ID '$($Result.processId)'."
    }
    if ($Result.callbackThreadId -le 0 -or $null -ne $Result.managedException) {
        throw "$ExpectedMode callback did not complete cleanly: $($Result | ConvertTo-Json -Compress)"
    }
}

Push-Location $repositoryRoot
try {
    & ./eng/verify-stage0-1.ps1 -Configuration $Configuration

    & (Join-Path $PSScriptRoot 'validation-dotnet.ps1') build 'tests/Probes/FxDbg.ClrDebug.Probe/FxDbg.ClrDebug.Probe.csproj' --configuration $Configuration --no-incremental
    if ($LASTEXITCODE -ne 0) {
        throw "ClrDebug probe build failed with exit code $LASTEXITCODE."
    }

    $targetPath = Join-Path $repositoryRoot "tests/Debuggees/Fx40.Console.x86/bin/$Configuration/net40/Fx40.Console.x86.exe"
    $probePath = Join-Path $repositoryRoot "tests/Probes/FxDbg.ClrDebug.Probe/bin/$Configuration/net48/FxDbg.ClrDebug.Probe.exe"

    $launchResult = Invoke-Probe -ProbePath $probePath -Arguments @(
        'launch', '--target', $targetPath, '--timeout-seconds', '15'
    )
    Assert-ProbeResult -Result $launchResult -ExpectedMode 'launch' -ExpectedProcessId $null

    $attachTarget = Start-Process -FilePath $targetPath -ArgumentList @('--wait-milliseconds', '30000') -PassThru
    try {
        Start-Sleep -Milliseconds 500
        if ($attachTarget.HasExited) {
            throw 'Attach target exited before the probe could attach.'
        }

        $attachResult = Invoke-Probe -ProbePath $probePath -Arguments @(
            'attach', '--pid', $attachTarget.Id.ToString(), '--timeout-seconds', '15'
        )
        Assert-ProbeResult -Result $attachResult -ExpectedMode 'attach' -ExpectedProcessId $attachTarget.Id
        if ($attachTarget.HasExited) {
            throw 'Attach verification terminated the target process instead of detaching.'
        }
    }
    finally {
        if (-not $attachTarget.HasExited) {
            $attachTarget.Kill()
            [void]$attachTarget.WaitForExit(5000)
        }
        $attachTarget.Dispose()
    }

    Write-Host 'Stage 0-2 verification passed: ClrDebug 0.4.2 produced CreateProcess callbacks for x86 launch and attach without managed exceptions.'
}
finally {
    Pop-Location
}
