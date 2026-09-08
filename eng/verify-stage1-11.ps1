param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'validation-reuse.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$cliPath = Join-Path $repoRoot "src/FxDbg.Cli/bin/$Configuration/net10.0-windows/fxdbg.dll"
function Invoke-FxCli([string[]]$CliArguments) {
    $output = & dotnet $cliPath @CliArguments
    if ($LASTEXITCODE -ne 0) { throw "CLI command failed: $($CliArguments[0])" }
    $result = $output | ConvertFrom-Json
    if (-not $result.ok) { throw 'CLI response was not successful.' }
    return $result
}
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Process-FromId([int]$ProcessId) {
    $process = [Diagnostics.Process]::GetProcessById($ProcessId)
    $null = $process.Handle
    return $process
}
Push-Location $repoRoot
try {
    & (Join-Path $PSScriptRoot 'validation-dotnet.ps1') build FxDbg.sln --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & (Join-Path $PSScriptRoot 'validation-dotnet.ps1') test tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj --configuration $Configuration --no-build --no-restore --filter FullyQualifiedName~CliCommandTests
    if ($LASTEXITCODE -ne 0) { throw 'CLI parser tests failed.' }
    foreach ($architecture in @('x86','x64')) {
        $name = if ($architecture -eq 'x86') { 'Fx40.ModuleLifecycle.x86' } else { 'Fx40.ModuleLifecycle' }
        $targetPath = Join-Path $repoRoot "tests/Debuggees/$name/bin/$Configuration/net40/$name.exe"
        $source = Join-Path $repoRoot 'tests/Debuggees/Fx40.ModuleLifecycle/Program.cs'
        $lines = Get-Content -LiteralPath $source
        $line = 1 + [Array]::FindIndex($lines, [Predicate[string]]{param($text) $text.Contains('// VARIABLE_BREAKPOINT')})
        $launch = Invoke-FxCli @('launch','--exe',$targetPath,'--arg','--variables','--stop-at-entry','--arch','auto')
        $session = $launch.sessionId
        $owned = @((Process-FromId $launch.result.hostProcessId), (Process-FromId $launch.result.engineProcessId), (Process-FromId $launch.result.target.processId))
        try {
            Require ($launch.result.target.architecture -eq $architecture -and $launch.result.target.sessionState -eq 'stopped') 'CLI launch routing or stop-at-entry failed.'
            $breakpoint = Invoke-FxCli @('break','--session',$session,'--file',$source,'--line',[string]$line)
            $null = Invoke-FxCli @('continue','--session',$session)
            $stop = Invoke-FxCli @('wait','--session',$session)
            Require ($stop.result.reason -eq 'breakpoint') 'CLI source breakpoint did not stop.'
            $threadId = [string]$stop.result.threadId
            $stack = Invoke-FxCli @('stack','--session',$session,'--thread',$threadId)
            $variables = Invoke-FxCli @('variables','--session',$session,'--frame',$stack.result[0].frameId,'--max-depth','0')
            Require (@($variables.result | Where-Object { $_.name -eq 'number' -and $_.displayValue -eq '42' }).Count -eq 1) 'CLI variable result differs from the shared reader.'
            $null = Invoke-FxCli @('remove-break','--session',$session,'--breakpoint',$breakpoint.result.breakpointId)
            $null = Invoke-FxCli @('step','--session',$session,'--kind','over','--thread',$threadId)
            $step = Invoke-FxCli @('wait','--session',$session)
            Require ($step.result.reason -eq 'step') 'CLI source step failed.'
            $events = Invoke-FxCli @('events','--session',$session)
            Require (@($events.result | Where-Object kind -eq 'stopped').Count -ge 2) 'CLI event polling missed stops.'
            $null = Invoke-FxCli @('detach','--session',$session)
            foreach ($process in $owned) { Require ($process.WaitForExit(12000)) 'CLI detach left a test process running.' }
            Require ($owned[2].ExitCode -eq 0) 'Target detected a variable observation side effect.'
        }
        finally {
            foreach ($process in $owned) { if (-not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(5000) }; $process.Dispose() }
        }
        $console = Join-Path $repoRoot "tests/Debuggees/Fx40.Console.$architecture/bin/$Configuration/net40/Fx40.Console.$architecture.exe"
        $start = [Diagnostics.ProcessStartInfo]::new($console)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.ArgumentList.Add('--wait-milliseconds')
        $start.ArgumentList.Add('30000')
        $target = [Diagnostics.Process]::Start($start)
        try {
            Start-Sleep -Milliseconds 500
            $attach = Invoke-FxCli @('attach','--pid',[string]$target.Id,'--arch','auto')
            $null = Invoke-FxCli @('pause','--session',$attach.sessionId)
            $threads = Invoke-FxCli @('threads','--session',$attach.sessionId)
            Require (@($threads.result).Count -gt 0) 'CLI attach did not enumerate managed threads.'
            $null = Invoke-FxCli @('detach','--session',$attach.sessionId)
            Require (-not $target.HasExited) 'CLI attach/detach terminated the target.'
        }
        finally { if (-not $target.HasExited) { $target.Kill(); $null = $target.WaitForExit(5000) }; $target.Dispose() }
        Write-Host "PASS: $architecture CLI launch/attach/break/continue/step/stack/variables/detach."
    }
    Write-Host 'Stage 1-11 verification passed.'
}
finally { Pop-Location }
