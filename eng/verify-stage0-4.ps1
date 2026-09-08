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
$logDirectory = Join-Path $artifactsRoot 'stage0-4'

function Invoke-CallbackProbe {
    param(
        [string]$ProbePath,
        [string]$TargetPath,
        [string]$LogLevel,
        [string]$LogPath
    )

    $output = & $ProbePath launch --target $TargetPath --timeout-seconds 15 --log-level $LogLevel --log-file $LogPath
    if ($LASTEXITCODE -ne 0) {
        throw "Callback probe failed with exit code $LASTEXITCODE."
    }

    return ($output | ConvertFrom-Json)
}

Push-Location $repositoryRoot
try {
    & ./eng/verify-stage0-1.ps1 -Configuration $Configuration
    & (Join-Path $PSScriptRoot 'validation-dotnet.ps1') build 'tests/Probes/FxDbg.ClrDebug.Probe/FxDbg.ClrDebug.Probe.csproj' --configuration $Configuration --no-incremental
    if ($LASTEXITCODE -ne 0) {
        throw "ClrDebug probe build failed with exit code $LASTEXITCODE."
    }

    [void](New-Item -ItemType Directory -Force -Path $logDirectory)
    $infoLogPath = Join-Path $logDirectory 'callbacks.info.jsonl'
    $traceLogPath = Join-Path $logDirectory 'callbacks.trace.jsonl'
    $offLogPath = Join-Path $logDirectory 'callbacks.off.jsonl'
    foreach ($path in @($infoLogPath, $traceLogPath, $offLogPath)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }

    $probePath = Join-Path $repositoryRoot "tests/Probes/FxDbg.ClrDebug.Probe/bin/$Configuration/net48/FxDbg.ClrDebug.Probe.exe"
    $targetPath = Join-Path $repositoryRoot "tests/Debuggees/Fx40.Console.x86/bin/$Configuration/net40/Fx40.Console.x86.exe"
    $result = Invoke-CallbackProbe -ProbePath $probePath -TargetPath $targetPath -LogLevel 'info' -LogPath $infoLogPath

    $expectedCallbacks = @('CreateProcess', 'CreateAppDomain', 'LoadAssembly', 'LoadModule', 'CreateThread')
    foreach ($expectedCallback in $expectedCallbacks) {
        if ($result.callbackKinds -notcontains $expectedCallback) {
            throw "Missing core callback '$expectedCallback': $($result | ConvertTo-Json -Compress -Depth 4)"
        }
    }
    if ($result.callbackThreadIds.Count -ne 1) {
        throw "Callbacks used more than one managed callback thread: $($result.callbackThreadIds -join ', ')"
    }
    if ($result.callbackThreadIds[0] -eq $result.commandThreadId) {
        throw 'Callbacks executed on the command thread instead of the dedicated managed callback thread.'
    }
    if ($result.continueCount -ne ($result.callbackCount - 1)) {
        throw "Continue count $($result.continueCount) did not match non-exit callback count $($result.callbackCount - 1)."
    }
    if ($result.maxCallbackDurationMilliseconds -ge 50) {
        throw "A callback spent $($result.maxCallbackDurationMilliseconds) ms before returning; expected less than 50 ms."
    }

    $logEntries = @(Get-Content -LiteralPath $infoLogPath | ForEach-Object { $_ | ConvertFrom-Json })
    foreach ($expectedCallback in $expectedCallbacks) {
        if ($logEntries.callback -notcontains $expectedCallback) {
            throw "Info log is missing '$expectedCallback'."
        }
    }
    foreach ($entry in $logEntries) {
        if ([string]::IsNullOrWhiteSpace($entry.sessionId) -or $entry.processId -le 0 -or
            $entry.callbackThreadId -le 0 -or $entry.commandThreadId -le 0) {
            throw "Log entry lacks session/process/thread context: $($entry | ConvertTo-Json -Compress)"
        }
    }

    $traceResult = Invoke-CallbackProbe -ProbePath $probePath -TargetPath $targetPath -LogLevel 'trace' -LogPath $traceLogPath
    $traceEntries = @(Get-Content -LiteralPath $traceLogPath | ForEach-Object { $_ | ConvertFrom-Json })
    if ($traceEntries.Count -ne $traceResult.callbackCount -or $traceEntries.callback -notcontains 'ExitProcess') {
        throw 'Trace logging did not include the complete callback stream.'
    }
    if ($traceEntries.Count -le $logEntries.Count) {
        throw 'Trace logging was not more detailed than info logging.'
    }

    [void](Invoke-CallbackProbe -ProbePath $probePath -TargetPath $targetPath -LogLevel 'off' -LogPath $offLogPath)
    if (Test-Path -LiteralPath $offLogPath) {
        throw 'Log level off still created a callback log file.'
    }

    Write-Host 'Stage 0-4 verification passed: five core callbacks were queued on one callback thread and configurable logging preserved full context.'
}
finally {
    Pop-Location
}
