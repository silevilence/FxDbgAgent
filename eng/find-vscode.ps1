param([string]$CodePath = 'code')
$ErrorActionPreference = 'Stop'
$resolved = (Get-Command $CodePath -ErrorAction Stop).Source
if ([IO.Path]::GetExtension($resolved) -eq '.cmd') {
    $resolved = Join-Path (Split-Path -Parent (Split-Path -Parent $resolved)) 'Code.exe'
}
if (-not (Test-Path -LiteralPath $resolved -PathType Leaf) -or [IO.Path]::GetExtension($resolved) -ne '.exe') { throw 'Specify the real VS Code executable with -CodePath.' }
[IO.Path]::GetFullPath($resolved)
