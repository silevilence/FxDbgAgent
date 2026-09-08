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

function Invoke-ProbeJson {
    param(
        [string]$ProbePath,
        [string[]]$Arguments,
        [bool]$ExpectSuccess
    )

    $lines = @(& $ProbePath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output = ($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    if ($ExpectSuccess -and $exitCode -ne 0) {
        throw "Probe unexpectedly failed with exit code ${exitCode}: $output"
    }
    if (-not $ExpectSuccess -and $exitCode -eq 0) {
        throw "Probe unexpectedly succeeded: $output"
    }

    return ($output | ConvertFrom-Json)
}

function Assert-SuccessResult {
    param(
        [object]$Result,
        [string]$Mode,
        [string]$Architecture,
        [Nullable[int]]$ProcessId
    )

    if ($Result.mode -ne $Mode -or $Result.callback -ne 'CreateProcess') {
        throw "Unexpected $Mode callback result: $($Result | ConvertTo-Json -Compress)"
    }
    if ($Result.runtimeVersion -ne 'v4.0.30319' -or $Result.debuggerArchitecture -ne $Architecture) {
        throw "Unexpected $Mode runtime/architecture: $($Result | ConvertTo-Json -Compress)"
    }
    if ($Result.processId -le 0 -or ($null -ne $ProcessId -and $Result.processId -ne $ProcessId)) {
        throw "Unexpected $Mode process ID: $($Result | ConvertTo-Json -Compress)"
    }
}

function Assert-RejectedAttach {
    param(
        [string]$ProbePath,
        [System.Diagnostics.Process]$Target,
        [string]$ExpectedCode
    )

    $result = Invoke-ProbeJson -ProbePath $ProbePath -Arguments @(
        'attach', '--pid', $Target.Id.ToString(), '--timeout-seconds', '15'
    ) -ExpectSuccess $false
    if ($result.code -ne $ExpectedCode) {
        throw "Expected rejection '$ExpectedCode', got: $($result | ConvertTo-Json -Compress)"
    }
    if ([string]::IsNullOrWhiteSpace($result.message)) {
        throw "Rejected attach '$ExpectedCode' did not include an actionable message."
    }
    if ($Target.HasExited) {
        throw "Rejected attach '$ExpectedCode' terminated target process $($Target.Id)."
    }
}

function Stop-OwnedProcess {
    param([System.Diagnostics.Process]$Process)

    if (-not $Process.HasExited) {
        $Process.Kill()
        [void]$Process.WaitForExit(5000)
    }
    $Process.Dispose()
}

Push-Location $repositoryRoot
try {
    & ./eng/verify-stage0-1.ps1 -Configuration $Configuration

    $probeProjects = @(
        'tests/Probes/FxDbg.ClrDebug.Probe/FxDbg.ClrDebug.Probe.csproj',
        'tests/Probes/FxDbg.ClrDebug.Probe.x64/FxDbg.ClrDebug.Probe.x64.csproj'
    )
    foreach ($probeProject in $probeProjects) {
        & (Join-Path $PSScriptRoot 'validation-dotnet.ps1') build $probeProject --configuration $Configuration --no-incremental
        if ($LASTEXITCODE -ne 0) {
            throw "Probe build failed for $probeProject with exit code $LASTEXITCODE."
        }
    }

    $probes = @{
        x86 = Join-Path $repositoryRoot "tests/Probes/FxDbg.ClrDebug.Probe/bin/$Configuration/net48/FxDbg.ClrDebug.Probe.exe"
        x64 = Join-Path $repositoryRoot "tests/Probes/FxDbg.ClrDebug.Probe.x64/bin/$Configuration/net48/FxDbg.ClrDebug.Probe.x64.exe"
    }
    $targets = @{
        x86 = Join-Path $repositoryRoot "tests/Debuggees/Fx40.Console.x86/bin/$Configuration/net40/Fx40.Console.x86.exe"
        x64 = Join-Path $repositoryRoot "tests/Debuggees/Fx40.Console.x64/bin/$Configuration/net40/Fx40.Console.x64.exe"
    }

    foreach ($architecture in @('x86', 'x64')) {
        $launchResult = Invoke-ProbeJson -ProbePath $probes[$architecture] -Arguments @(
            'launch', '--target', $targets[$architecture], '--timeout-seconds', '15'
        ) -ExpectSuccess $true
        Assert-SuccessResult -Result $launchResult -Mode 'launch' -Architecture $architecture -ProcessId $null

        $attachTarget = Start-Process -FilePath $targets[$architecture] -ArgumentList @(
            '--wait-milliseconds', '30000'
        ) -PassThru -WindowStyle Hidden
        try {
            Start-Sleep -Milliseconds 500
            Assert-SuccessResult -Result (
                Invoke-ProbeJson -ProbePath $probes[$architecture] -Arguments @(
                    'attach', '--pid', $attachTarget.Id.ToString(), '--timeout-seconds', '15'
                ) -ExpectSuccess $true
            ) -Mode 'attach' -Architecture $architecture -ProcessId $attachTarget.Id
            if ($attachTarget.HasExited) {
                throw "$architecture attach terminated its target."
            }
        }
        finally {
            Stop-OwnedProcess -Process $attachTarget
        }
    }

    foreach ($pair in @(
        @{ Probe = 'x86'; Target = 'x64' },
        @{ Probe = 'x64'; Target = 'x86' }
    )) {
        $crossTarget = Start-Process -FilePath $targets[$pair.Target] -ArgumentList @(
            '--wait-milliseconds', '30000'
        ) -PassThru -WindowStyle Hidden
        try {
            Start-Sleep -Milliseconds 500
            Assert-RejectedAttach -ProbePath $probes[$pair.Probe] -Target $crossTarget -ExpectedCode 'architecture_mismatch'
        }
        finally {
            Stop-OwnedProcess -Process $crossTarget
        }
    }

    $nativeTarget = Start-Process -FilePath "$env:SystemRoot/System32/ping.exe" -ArgumentList @(
        '-n', '30', '127.0.0.1'
    ) -PassThru -WindowStyle Hidden
    try {
        Start-Sleep -Milliseconds 500
        Assert-RejectedAttach -ProbePath $probes.x64 -Target $nativeTarget -ExpectedCode 'not_managed_process'
    }
    finally {
        Stop-OwnedProcess -Process $nativeTarget
    }

    $coreTargetPath = Join-Path $repositoryRoot "tests/Debuggees/Core80.Console.x64/bin/$Configuration/net8.0/Core80.Console.x64.exe"
    $coreTarget = Start-Process -FilePath $coreTargetPath -ArgumentList @(
        '--wait-milliseconds', '30000'
    ) -PassThru -WindowStyle Hidden
    try {
        Start-Sleep -Milliseconds 500
        Assert-RejectedAttach -ProbePath $probes.x64 -Target $coreTarget -ExpectedCode 'coreclr_not_supported'
    }
    finally {
        Stop-OwnedProcess -Process $coreTarget
    }

    $legacyTargetPath = Join-Path $repositoryRoot "tests/Debuggees/Fx35.Console.x86/bin/$Configuration/net35/Fx35.Console.x86.exe"
    $legacyTarget = Start-Process -FilePath $legacyTargetPath -ArgumentList @(
        '--wait-milliseconds', '30000'
    ) -PassThru -WindowStyle Hidden
    try {
        Start-Sleep -Milliseconds 500
        Assert-RejectedAttach -ProbePath $probes.x86 -Target $legacyTarget -ExpectedCode 'unsupported_clr_version'
    }
    finally {
        Stop-OwnedProcess -Process $legacyTarget
    }

    Write-Host 'Stage 0-3 verification passed: launch/attach x86+x64 succeeded and all rejection paths preserved their targets.'
}
finally {
    Pop-Location
}
