# Shared by the local MCP publisher and release packager. Paths are relative to the bundle root.
function Get-BundleHashes([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory)
    Get-ChildItem -LiteralPath $root -Recurse -File -Force |
        Where-Object { $_.FullName -ne (Join-Path $root 'bundle-manifest.sha256') } |
        Sort-Object FullName | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
            '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
        }
}
