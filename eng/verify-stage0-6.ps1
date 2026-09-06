[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$env:DOTNET_CLI_HOME = Join-Path $artifactsRoot 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $artifactsRoot 'nuget-packages'
$reportDirectory = Join-Path $artifactsRoot 'stage0-6'

function Invoke-BreakpointProbe {
    param(
        [string]$ProbePath,
        [string]$TargetPath,
        [string]$ModuleName,
        [string]$MethodToken,
        [int]$IlOffset
    )

    $output = @(& $ProbePath --target $TargetPath --module $ModuleName --method-token $MethodToken --il-offset $IlOffset --timeout-seconds 20)
    if ($LASTEXITCODE -ne 0) {
        throw "Breakpoint probe failed with exit code $LASTEXITCODE.`n$($output -join [Environment]::NewLine)"
    }

    $json = @($output | Where-Object { $_ -like '{"bindingState"*' } | Select-Object -Last 1)
    if ($json.Count -ne 1) {
        throw "Breakpoint probe did not emit exactly one result object.`n$($output -join [Environment]::NewLine)"
    }

    return ($json[0] | ConvertFrom-Json)
}

function Resolve-StackFrame {
    param(
        [string]$SymbolProbePath,
        [string]$AssemblyPath,
        [string]$PdbPath,
        [object]$Frame
    )

    $output = & $SymbolProbePath resolve --assembly $AssemblyPath --pdb $PdbPath --method-token $Frame.methodToken --il-offset $Frame.ilOffset
    if ($LASTEXITCODE -ne 0) {
        throw "Symbol resolver failed with exit code $LASTEXITCODE."
    }

    $resolved = $output | ConvertFrom-Json
    if ($resolved.status -ne 'loaded' -or @($resolved.mappings).Count -ne 1) {
        throw "Stack frame could not be resolved: $($resolved | ConvertTo-Json -Compress -Depth 5)"
    }

    $mapping = @($resolved.mappings)[0]
    return [pscustomobject]@{
        methodName = $mapping.methodName
        module = $Frame.module
        methodToken = $Frame.methodToken
        ilOffset = $Frame.ilOffset
        sourceFile = $mapping.sourceFile
        line = $mapping.startLine
        mapping = $Frame.mapping
    }
}

Push-Location $repositoryRoot
try {
    & ./eng/verify-stage0-5.ps1 -Configuration $Configuration
    & dotnet build 'tests/Probes/FxDbg.Breakpoint.Probe/FxDbg.Breakpoint.Probe.csproj' --configuration $Configuration --no-incremental
    if ($LASTEXITCODE -ne 0) {
        throw "Breakpoint probe build failed with exit code $LASTEXITCODE."
    }

    [void](New-Item -ItemType Directory -Force -Path $reportDirectory)
    $breakpointProbePath = Join-Path $repositoryRoot "tests/Probes/FxDbg.Breakpoint.Probe/bin/$Configuration/net48/FxDbg.Breakpoint.Probe.exe"
    $symbolProbePath = Join-Path $repositoryRoot "tests/Probes/FxDbg.Symbols.Probe/bin/$Configuration/net8.0-windows/FxDbg.Symbols.Probe.exe"
    $assemblyPath = Join-Path $repositoryRoot "tests/Debuggees/Fx40.Console.x86/bin/$Configuration/net40/Fx40.Console.x86.exe"
    $pdbPath = Join-Path $repositoryRoot "tests/Debuggees/Fx40.Console.x86/bin/$Configuration/net40/Fx40.Console.x86.pdb"
    $sourcePath = Join-Path $repositoryRoot 'tests/Debuggees/Fx40.Console.x86/Program.cs'
    $sourceLine = (Select-String -LiteralPath $sourcePath -SimpleMatch 'breakpointValueSink = 1;').LineNumber
    if ($null -eq $sourceLine) {
        throw 'Could not locate the source breakpoint line.'
    }

    $bindingOutput = & $symbolProbePath map --assembly $assemblyPath --pdb $pdbPath --source $sourcePath --line $sourceLine
    $binding = $bindingOutput | ConvertFrom-Json
    $targetMapping = @($binding.mappings | Where-Object { $_.methodName -eq 'FxDbg.Debuggees.ConsoleX86.Program.BreakpointTarget' })
    if ($binding.status -ne 'loaded' -or $targetMapping.Count -ne 1 -or @($targetMapping[0].ilOffsets).Count -eq 0) {
        throw "Source breakpoint did not bind to BreakpointTarget: $($binding | ConvertTo-Json -Compress -Depth 5)"
    }

    $methodToken = $targetMapping[0].methodToken
    $ilOffset = [int]@($targetMapping[0].ilOffsets)[0]
    $hit = Invoke-BreakpointProbe -ProbePath $breakpointProbePath -TargetPath $assemblyPath -ModuleName 'Fx40.Console.x86.exe' -MethodToken $methodToken -IlOffset $ilOffset
    if (-not $hit.hit -or $hit.bindingState -ne 'verified' -or ($hit.bindingStates -join ',') -ne 'pending,verified') {
        throw "Expected pending -> verified -> hit: $($hit | ConvertTo-Json -Compress -Depth 6)"
    }
    if ($hit.debuggerArchitecture -ne 'x86') {
        throw "Expected the x86 breakpoint probe to report its actual architecture: $($hit | ConvertTo-Json -Compress -Depth 6)"
    }
    if ($hit.callbackThreadId -eq $hit.commandThreadId) {
        throw 'Breakpoint callback work was not separated from the command thread.'
    }
    if ($hit.continueCount -ne ($hit.callbackCount - 1)) {
        throw "Callback/Continue counts are not paired: $($hit | ConvertTo-Json -Compress -Depth 5)"
    }

    $targetFrames = @($hit.frames | Where-Object { [IO.Path]::GetFileName($_.module) -eq 'Fx40.Console.x86.exe' })
    if ($targetFrames.Count -lt 10) {
        throw "Expected at least 10 managed target frames, found $($targetFrames.Count)."
    }

    $resolvedFrames = @($targetFrames | ForEach-Object {
        Resolve-StackFrame -SymbolProbePath $symbolProbePath -AssemblyPath $assemblyPath -PdbPath $pdbPath -Frame $_
    })
    $firstTen = @($resolvedFrames | Select-Object -First 10)
    $expectedMethods = @(
        'FxDbg.Debuggees.ConsoleX86.Program.BreakpointTarget',
        'FxDbg.Debuggees.ConsoleX86.Program.StackLevel12',
        'FxDbg.Debuggees.ConsoleX86.Program.StackLevel11',
        'FxDbg.Debuggees.ConsoleX86.Program.StackLevel10',
        'FxDbg.Debuggees.ConsoleX86.Program.StackLevel09',
        'FxDbg.Debuggees.ConsoleX86.Program.StackLevel08',
        'FxDbg.Debuggees.ConsoleX86.Program.StackLevel07',
        'FxDbg.Debuggees.ConsoleX86.Program.StackLevel06',
        'FxDbg.Debuggees.ConsoleX86.Program.StackLevel05',
        'FxDbg.Debuggees.ConsoleX86.Program.StackLevel04'
    )
    foreach ($frame in $firstTen) {
        if ([string]::IsNullOrWhiteSpace($frame.methodName) -or
            [string]::IsNullOrWhiteSpace($frame.module) -or
            [string]::IsNullOrWhiteSpace($frame.sourceFile) -or
            $null -eq $frame.ilOffset -or
            $null -eq $frame.line) {
            throw "A required stack field is missing: $($frame | ConvertTo-Json -Compress)"
        }
    }
    if ($firstTen[0].methodName -ne 'FxDbg.Debuggees.ConsoleX86.Program.BreakpointTarget' -or
        $firstTen[0].line -ne $sourceLine -or
        $firstTen[0].ilOffset -ne $ilOffset) {
        throw "Breakpoint stopped at the wrong source location: $($firstTen[0] | ConvertTo-Json -Compress)"
    }
    $actualMethods = @($firstTen | ForEach-Object { $_.methodName })
    if (($actualMethods -join ',') -ne ($expectedMethods -join ',')) {
        throw "ICorDebug stack order mismatch. Expected $($expectedMethods -join ' -> '), actual $($actualMethods -join ' -> ')."
    }

    $pending = Invoke-BreakpointProbe -ProbePath $breakpointProbePath -TargetPath $assemblyPath -ModuleName 'NeverLoaded.Managed.dll' -MethodToken $methodToken -IlOffset $ilOffset
    if ($pending.bindingState -ne 'pending' -or $pending.hit) {
        throw "Expected an unloaded module to remain pending: $($pending | ConvertTo-Json -Compress -Depth 5)"
    }

    $unresolved = Invoke-BreakpointProbe -ProbePath $breakpointProbePath -TargetPath $assemblyPath -ModuleName 'Fx40.Console.x86.exe' -MethodToken '0x0600FFFF' -IlOffset 0
    if ($unresolved.bindingState -ne 'unresolved' -or $unresolved.hit -or [string]::IsNullOrWhiteSpace($unresolved.bindingError)) {
        throw "Expected an invalid method token to become unresolved: $($unresolved | ConvertTo-Json -Compress -Depth 5)"
    }

    $stackReport = [pscustomobject]@{
        configuration = $Configuration
        binding = [pscustomobject]@{
            sourceFile = $sourcePath
            line = $sourceLine
            methodToken = $methodToken
            ilOffset = $ilOffset
            states = @($hit.bindingStates)
        }
        hit = $true
        frames = $resolvedFrames
    }
    $stackReport | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reportDirectory "stack-$($Configuration.ToLowerInvariant()).json") -Encoding utf8
    $pending | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reportDirectory "pending-$($Configuration.ToLowerInvariant()).json") -Encoding utf8
    $unresolved | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reportDirectory "unresolved-$($Configuration.ToLowerInvariant()).json") -Encoding utf8

    $cdbPath = & (Join-Path $PSScriptRoot 'find-windows-debugger.ps1') -Architecture x86

    $start = [Diagnostics.ProcessStartInfo]::new($cdbPath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-y', $reportDirectory, '-o', '-g', '-G', '-c',
        '.load C:\Windows\Microsoft.NET\Framework\v4.0.30319\sos.dll; !clrstack; q', $assemblyPath, '--windbg-probe')) {
        $start.ArgumentList.Add($argument)
    }
    $debugger = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $debugger.StandardOutput.ReadToEndAsync()
        $stderr = $debugger.StandardError.ReadToEndAsync()
        $timedOut = -not $debugger.WaitForExit(30000)
        if ($timedOut) { $debugger.Kill($true); $debugger.WaitForExit() }
        $cdbOutput = ($stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()) -split '\r?\n'
        if ($timedOut) { throw 'CDB exceeded 30 seconds; its test process tree was cleaned up.' }
        if ($debugger.ExitCode -ne 0) { throw 'CDB/SOS comparison failed.' }
    }
    finally { $debugger.Dispose() }
    $cdbText = $cdbOutput -join [Environment]::NewLine
    $cdbMethods = @($cdbOutput | ForEach-Object {
        if ($_ -match 'FxDbg\.Debuggees\.ConsoleX86\.Program\.(BreakpointTarget|StackLevel(?:0[1-9]|1[0-2]))\(') {
            "FxDbg.Debuggees.ConsoleX86.Program.$($Matches[1])"
        }
    } | Select-Object -First 10)
    if (($cdbMethods -join ',') -ne ($expectedMethods -join ',')) {
        throw "WinDbg/SOS stack order mismatch. Expected $($expectedMethods -join ' -> '), actual $($cdbMethods -join ' -> ')."
    }
    $cdbText | Set-Content -LiteralPath (Join-Path $reportDirectory "windbg-$($Configuration.ToLowerInvariant()).txt") -Encoding utf8

    Write-Host "Stage 0-6 verification passed: source line $sourceLine hit at IL $ilOffset with $($resolvedFrames.Count) symbolized target frames; pending/unresolved and WinDbg/SOS comparison passed."
}
finally {
    Pop-Location
}
