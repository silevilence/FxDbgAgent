param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot "artifacts/dap/$Configuration" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$env:DOTNET_CLI_HOME = Join-Path $repoRoot 'artifacts/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot 'artifacts/nuget-packages'
dotnet build (Join-Path $repoRoot 'FxDbg.sln') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
dotnet publish (Join-Path $repoRoot 'src/FxDbg.DapHost/FxDbg.DapHost.csproj') -c $Configuration --no-restore --no-build -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'DAP publish failed.' }
$engineDestination = Join-Path $OutputDirectory 'engines'
$null = New-Item -ItemType Directory -Path $engineDestination -Force
Get-ChildItem -LiteralPath (Join-Path $repoRoot "src/FxDbg.Engine/bin/$Configuration/net48") -File | Copy-Item -Destination $engineDestination
@(Get-ChildItem -LiteralPath (Join-Path $repoRoot "src/FxDbg.Engine/bin/$Configuration/net48") -File | ForEach-Object Name) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $engineDestination 'engine-manifest.json') -Encoding utf8
Write-Host "DAP bundle: $OutputDirectory"
