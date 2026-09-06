param([Parameter(Mandatory)][ValidateSet('x86','x64')][string]$Architecture)
$ErrorActionPreference = 'Stop'
$directory = $env:FXDBG_DEBUGGERS_DIRECTORY
if (-not $directory) { $directory = 'C:/Program Files (x86)/Windows Kits/10/Debuggers' }
if (-not [IO.Path]::IsPathFullyQualified($directory)) { throw 'FXDBG_DEBUGGERS_DIRECTORY must be absolute.' }
$debugger = Join-Path $directory "$Architecture/cdb.exe"
if (-not (Test-Path -LiteralPath $debugger -PathType Leaf)) {
    throw "The $Architecture Windows debugger is required for independent stack comparison. Install Debugging Tools or set FXDBG_DEBUGGERS_DIRECTORY to a directory containing x86/cdb.exe and x64/cdb.exe."
}
[IO.Path]::GetFullPath($debugger)
