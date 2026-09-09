param(
    [Parameter(Mandatory)][ValidatePattern('^[vV][0-9A-Za-z.+-]+$')][string]$Tag,
    [Parameter(Mandatory)][string]$BundleDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'bundle-manifest.ps1')
$bundle = [IO.Path]::GetFullPath($BundleDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output -eq $bundle -or $output.StartsWith($bundle + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be outside BundleDirectory.'
}
$required = @('fxdbg-mcp.exe','fxdbg-mcp.dll','fxdbg-mcp.pdb','fxdbg-mcp.deps.json','fxdbg-mcp.runtimeconfig.json',
    'fxdbg-dap.exe','fxdbg-dap.dll','fxdbg-dap.pdb','fxdbg-dap.deps.json','fxdbg-dap.runtimeconfig.json',
    'ModelContextProtocol.dll','ModelContextProtocol.Core.dll','engines/engine-manifest.json',
    'engines/FxDbg.Engine.x86.exe','engines/FxDbg.Engine.x64.exe','engines/ClrDebug.dll',
    'engines/Microsoft.DiaSymReader.dll','engines/Microsoft.DiaSymReader.Native.x86.dll','engines/Microsoft.DiaSymReader.Native.amd64.dll',
    'skills/fxdbg-agent/SKILL.md','skills/fxdbg-agent/references/workflow.md','skills/fxdbg-agent/references/mcp-tools.md','bundle-manifest.sha256')
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $bundle $relative) -PathType Leaf)) { throw "Incomplete MCP/DAP bundle: missing $relative. Run eng/publish-mcp.ps1 and eng/publish-dap.ps1 -Configuration Release into the same fresh directory." }
}
$engineRoot = Join-Path $bundle 'engines'
$engineFiles = @(Get-ChildItem -LiteralPath $engineRoot -File | Where-Object Name -ne 'engine-manifest.json' | ForEach-Object Name)
$manifestFiles = @(Get-Content -LiteralPath (Join-Path $engineRoot 'engine-manifest.json') -Raw | ConvertFrom-Json)
if (Compare-Object $engineFiles $manifestFiles) { throw 'Engine file list does not match engine-manifest.json.' }
foreach ($assembly in Get-ChildItem -LiteralPath $bundle -Recurse -File | Where-Object { $_.Name -match '^(FxDbg\..*|fxdbg-(mcp|dap))\.(dll|exe)$' }) {
    $pdb = [IO.Path]::ChangeExtension($assembly.FullName, '.pdb')
    if (-not (Test-Path -LiteralPath $pdb -PathType Leaf)) { throw "Missing matching PDB: $pdb" }
    if ($assembly.DirectoryName -eq $engineRoot) {
        $header = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($pdb), 0, 24)
        if (-not $header.StartsWith('Microsoft C/C++ MSF 7.00')) { throw "Engine PDB must be Windows PDB (MSF 7.00): $pdb" }
    }
}
$recorded = @(Get-Content -LiteralPath (Join-Path $bundle 'bundle-manifest.sha256'))
$actual = @(Get-BundleHashes $bundle)
if (Compare-Object $recorded $actual) { throw 'Bundle SHA256 mismatch. Re-publish to a fresh directory; do not package modified outputs.' }
$null = New-Item -ItemType Directory -Path $output -Force
$zipPath = Join-Path $output "fxdbg-$($Tag.Substring(1)).zip"
# CreateFromDirectory includes every file (including hidden files), without a wrapper directory.
[IO.Compression.ZipFile]::CreateFromDirectory($bundle, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $zipped = @(foreach ($entry in $archive.Entries) {
        if (-not $entry.Name) { continue }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        '{0}  {1}' -f $hash, $entry.FullName.Replace('\', '/')
    })
    $expected = $actual + ('{0}  bundle-manifest.sha256' -f (Get-FileHash -LiteralPath (Join-Path $bundle 'bundle-manifest.sha256')).Hash.ToLowerInvariant())
    if (Compare-Object $expected $zipped) { throw 'ZIP content/hash mismatch with the published directory.' }
} finally { $archive.Dispose() }
Write-Host "Checked $($zipped.Count) files: $zipPath"
Write-Host "ZIP SHA256: $((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash)"
