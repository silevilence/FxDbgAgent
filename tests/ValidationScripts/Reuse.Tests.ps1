# Run in a fresh pwsh process. This suite uses fake build/test commands and isolated files;
# it never builds the solution, launches a debuggee, installs a service or starts IIS.
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ('FxDbg.Validation.ValidationProcess' -as [type]) { throw 'Use a fresh pwsh process for this isolated suite.' }
Add-Type @'
using System.Collections.Generic;
using System.IO;
namespace FxDbg.Validation {
    public static class ValidationProcess {
        public static List<string[]> Calls = new List<string[]>();
        public static int ExitCode;
        public static int Run(string executable, string[] arguments, string directory, string log, int seconds) {
            Calls.Add(arguments);
            File.WriteAllText(log, "Fake build/test output.");
            return ExitCode;
        }
    }
}
'@
. (Join-Path $repoRoot 'eng/validation-reuse.ps1')
$validationRepoRoot=Join-Path $repoRoot ('artifacts/reuse-script-tests/'+[Guid]::NewGuid().ToString('N'))
$allowedRoot=[IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/reuse-script-tests'))+[IO.Path]::DirectorySeparatorChar
$previousRun=$env:FXDBG_VALIDATION_RUN
Remove-Item Env:FXDBG_VALIDATION_RUN -ErrorAction SilentlyContinue
$script:passthrough=[Collections.Generic.List[object]]::new()
function dotnet { $script:passthrough.Add(@($args)); $global:LASTEXITCODE=0 }
function Require([bool]$Condition,[string]$Message) { if(-not $Condition){throw $Message} }
function Throws([scriptblock]$Action,[string]$Pattern) {
    try { & $Action } catch { if($_.Exception.Message -notlike $Pattern){throw}; return }
    throw "Expected failure: $Pattern"
}
$owned=$null
try {
    foreach($path in @('src','tests','eng','extensions','skills','tests/FxDbg.UnitTests/bin/Debug','tests/FxDbg.UnitTests/bin/Release')) {
        $null=New-Item -ItemType Directory -Path (Join-Path $validationRepoRoot $path) -Force
    }
    $solution=Join-Path $validationRepoRoot 'FxDbg.sln'
    $project=Join-Path $validationRepoRoot 'tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj'
    '<Project />' | Set-Content -LiteralPath $project
    'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Unit", "tests\FxDbg.UnitTests\FxDbg.UnitTests.csproj", "{503CA4D0-14AB-47CA-AA50-B38CD260E3AF}"' | Set-Content -LiteralPath $solution
    foreach($configuration in @('Debug','Release')) {
        'AAAA' | Set-Content -LiteralPath (Join-Path $validationRepoRoot "tests/FxDbg.UnitTests/bin/$configuration/unit.dll") -NoNewline
    }
    $owned=Enter-ValidationRun
    Require ($null -eq (Enter-ValidationRun)) 'Nested entry must inherit the run.'
    Invoke-ValidationDotNet -Arguments @('build',$solution,'-c','Debug')
    Invoke-ValidationDotNet -Arguments @('build',$project,'--configuration','Debug','--no-incremental')
    Require ([FxDbg.Validation.ValidationProcess]::Calls.Count -eq 1) 'Solution/project builds must share one forced build.'
    Require ('--no-incremental' -in [FxDbg.Validation.ValidationProcess]::Calls[0]) 'First build must remain forced.'
    Invoke-ValidationDotNet -Arguments @('test',$project,'-c','Debug','--filter','FullyQualifiedName~CliCommandTests')
    Invoke-ValidationDotNet -Arguments @('test',$project,'-c','Debug','--no-build','--no-restore')
    Require ([FxDbg.Validation.ValidationProcess]::Calls.Count -eq 2) 'Filtered and full unit requests must share one full suite.'
    Require ('--filter' -notin [FxDbg.Validation.ValidationProcess]::Calls[1]) 'First filtered request must actually run the full suite.'
    Invoke-ValidationDotNet -Arguments @('build',$solution,'-c','Release')
    Require ([FxDbg.Validation.ValidationProcess]::Calls.Count -eq 3) 'Configurations must not share a build.'
    Invoke-ValidationDotNet -Arguments @('test',$project,'-c','Debug','--collect','Code Coverage')
    Invoke-ValidationDotNet -Arguments @('test',$project,'-c','Debug','--filter','invalid custom filter')
    Require ($script:passthrough.Count -eq 2) 'Coverage and unknown filters must execute unchanged.'
    Require ('--collect' -in $script:passthrough[0]) 'Collector argument must be preserved.'
    $originalProject=[IO.File]::ReadAllBytes($project)
    '<Changed />' | Set-Content -LiteralPath $project
    Throws { Invoke-ValidationDotNet -Arguments @('build',$solution,'-c','Debug') } '*inputs changed*'
    [IO.File]::WriteAllBytes($project,$originalProject)
    $output=Join-Path $validationRepoRoot 'tests/FxDbg.UnitTests/bin/Debug/unit.dll'
    $timestamp=[IO.File]::GetLastWriteTimeUtc($output)
    [IO.File]::WriteAllText($output,'BBBB')
    [IO.File]::SetLastWriteTimeUtc($output,$timestamp)
    Throws { Confirm-ValidationRun $owned } '*hash changed*'
    [IO.File]::WriteAllText($output,'AAAA')
    [IO.File]::SetLastWriteTimeUtc($output,$timestamp)
    Confirm-ValidationRun $owned
    $extra=Join-Path (Split-Path -Parent $output) 'unexpected.dll'
    'extra' | Set-Content -LiteralPath $extra
    Throws { Invoke-ValidationDotNet -Arguments @('build',$solution,'-c','Debug') } '*output changed*'
    Remove-Item -LiteralPath $extra
    $closed=$owned
    Exit-ValidationRun $owned
    $owned=$null
    $env:FXDBG_VALIDATION_RUN=$closed
    Throws { Get-ValidationRun } '*closed run*'
    Remove-Item Env:FXDBG_VALIDATION_RUN
    $owned=Enter-ValidationRun
    Require ($owned -ne $closed) 'A new invocation must create a different run.'
    [FxDbg.Validation.ValidationProcess]::ExitCode=1
    Throws { Invoke-ValidationDotNet -Arguments @('build',$solution,'-c','Debug') } '*Shared build failed*'
    Require (-not (Test-Path -LiteralPath (Join-Path $owned 'build-Debug.json'))) 'Failed preparation must not be reusable.'
    [FxDbg.Validation.ValidationProcess]::ExitCode=0
    Invoke-ValidationDotNet -Arguments @('build',$solution,'-c','Debug')
    Confirm-ValidationRun $owned
    Exit-ValidationRun $owned
    $owned=$null
    $before=$script:passthrough.Count
    Invoke-ValidationDotNet -Arguments @('build',$solution,'-c','Debug','--no-incremental')
    Require ($script:passthrough.Count -eq $before+1) 'Standalone execution must not reuse previous results.'
    Write-Host 'Validation reuse boundary checks passed (fake commands only).'
} finally {
    if($owned){Exit-ValidationRun $owned}
    if($previousRun){$env:FXDBG_VALIDATION_RUN=$previousRun}else{Remove-Item Env:FXDBG_VALIDATION_RUN -ErrorAction SilentlyContinue}
    $resolved=[IO.Path]::GetFullPath($validationRepoRoot)
    if(-not $resolved.StartsWith($allowedRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing cleanup outside isolated fixture directory.'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
