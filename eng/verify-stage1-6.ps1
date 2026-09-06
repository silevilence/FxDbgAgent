param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    dotnet build FxDbg.sln --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    foreach ($architecture in @('x86', 'x64')) {
        $runner = Join-Path $repoRoot "tests/FxDbg.IntegrationTests/bin/$Configuration/net48/FxDbg.IntegrationTests.$architecture.exe"
        & $runner $repoRoot $Configuration stack
        if ($LASTEXITCODE -ne 0) { throw "Real $architecture thread/stack test failed." }
        $cdb = & (Join-Path $PSScriptRoot 'find-windows-debugger.ps1') -Architecture $architecture
        $framework = if ($architecture -eq 'x86') { 'Framework' } else { 'Framework64' }
        $target = Join-Path $repoRoot "tests/Debuggees/Fx40.Console.$architecture/bin/$Configuration/net40/Fx40.Console.$architecture.exe"
        $start = [System.Diagnostics.ProcessStartInfo]::new($cdb)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('-y', (Join-Path $repoRoot 'artifacts/stage1-6'), '-o', '-g', '-G', '-c',
            ".load C:\Windows\Microsoft.NET\$framework\v4.0.30319\sos.dll; !clrstack; q", $target, '--stack-threads-windbg')) {
            $start.ArgumentList.Add($argument)
        }
        $debugger = [System.Diagnostics.Process]::Start($start)
        try {
            $stdout = $debugger.StandardOutput.ReadToEndAsync()
            $stderr = $debugger.StandardError.ReadToEndAsync()
            $timedOut = -not $debugger.WaitForExit(30000)
            if ($timedOut) { $debugger.Kill($true); $debugger.WaitForExit() }
            $output = ($stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()) -split '\r?\n'
            $output | Set-Content -LiteralPath "artifacts/stage1-6/cdb-$architecture-$($Configuration.ToLowerInvariant()).txt" -Encoding utf8
            if ($timedOut) { throw "CDB $architecture exceeded 30 seconds; test process tree was cleaned up." }
            if ($debugger.ExitCode -ne 0) { throw "CDB $architecture failed." }
        }
        finally { $debugger.Dispose() }
        $expected = @(Get-Content -LiteralPath "artifacts/stage1-6/stack-$architecture-$($Configuration.ToLowerInvariant()).txt")
        $actual = @($output | ForEach-Object {
            if ($_ -match '(FxDbg\.Debuggees\.ConsoleX(?:86|64)\.Program\.(?:BreakpointTarget|StackLevel\d+|Main))\(') { $Matches[1] }
        })
        $output | Set-Content -LiteralPath "artifacts/stage1-6/cdb-$architecture-$($Configuration.ToLowerInvariant()).txt" -Encoding utf8
        if ($expected.Count -ne 14 -or ($actual -join ',') -ne ($expected -join ',')) {
            throw "Product stack and CDB/SOS differ for $architecture. Expected $($expected -join ' -> '); actual $($actual -join ' -> ')."
        }
    }
    Write-Host 'Stage 1-6 verification passed: named threads, 14 source frames, paging, and independent x86/x64 CDB/SOS comparison.'
}
finally { Pop-Location }
