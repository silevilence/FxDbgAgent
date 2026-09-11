param([Parameter(Mandatory)][ValidateSet('Mcp','Dap')][string]$Entry, [ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'validation-reuse.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot "artifacts/$($Entry.ToLowerInvariant())/$Configuration" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$env:DOTNET_CLI_HOME = Join-Path $repoRoot 'artifacts/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot 'artifacts/nuget-packages'
$runDirectory = Get-ValidationRun
$publishKey = "publish-$Entry-$Configuration-" + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($OutputDirectory.ToLowerInvariant())))
if (Test-ValidationPreparation $publishKey) { $global:LASTEXITCODE=0; return }
& (Join-Path $PSScriptRoot 'validation-dotnet.ps1') build (Join-Path $repoRoot 'FxDbg.sln') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$publishArguments = @('publish',(Join-Path $repoRoot "src/FxDbg.${Entry}Host/FxDbg.${Entry}Host.csproj"),'-c',$Configuration,'--no-restore','--no-build','-o',$OutputDirectory)
if ($runDirectory) {
    $publishLog = Join-Path $runDirectory "$publishKey.log"
    $code = [FxDbg.Validation.ValidationProcess]::Run('dotnet',$publishArguments,$repoRoot,$publishLog,180)
    Get-Content -LiteralPath $publishLog | Write-Host
    if ($code -ne 0) { throw "$Entry publish failed: $publishLog" }
} else {
    dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "$Entry publish failed." }
}
$engineDestination = Join-Path $OutputDirectory 'engines'
$null = New-Item -ItemType Directory -Path $engineDestination -Force
Get-ChildItem -LiteralPath (Join-Path $repoRoot "src/FxDbg.Engine/bin/$Configuration/net48") -File | Copy-Item -Destination $engineDestination
@(Get-ChildItem -LiteralPath (Join-Path $repoRoot "src/FxDbg.Engine/bin/$Configuration/net48") -File | ForEach-Object Name) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $engineDestination 'engine-manifest.json') -Encoding utf8
if ($Entry -eq 'Mcp') {
    $skillDestination = Join-Path $OutputDirectory 'skills'
    $null = New-Item -ItemType Directory -Path $skillDestination -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'skills/fxdbg-agent') -Destination $skillDestination -Recurse -Force
}
. (Join-Path $PSScriptRoot 'bundle-manifest.ps1')
Get-BundleHashes $OutputDirectory | Set-Content -LiteralPath (Join-Path $OutputDirectory 'bundle-manifest.sha256') -Encoding utf8
if ($runDirectory) { Save-ValidationPreparation $publishKey @($OutputDirectory) $publishLog }
Write-Host "$Entry bundle: $OutputDirectory"
