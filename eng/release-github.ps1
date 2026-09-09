param(
    [Parameter(Mandatory)][ValidatePattern('^[vV][0-9A-Za-z.+-]+$')][string]$Tag,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$metadata = Get-Content -LiteralPath (Join-Path $OutputDirectory 'metadata.json') -Raw | ConvertFrom-Json
if ($metadata.tag -cne $Tag) { throw 'Release metadata belongs to a different tag. Run release-notes.ps1 again.' }
$notesPath = Join-Path $OutputDirectory 'notes.md'
$zipPath = Join-Path $OutputDirectory "fxdbg-$($Tag.Substring(1)).zip"
foreach ($path in @($notesPath, $zipPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release input: $path" }
}
# Querying the list distinguishes API/auth failures from an absent release.
$releaseJson = gh api --paginate "repos/{owner}/{repo}/releases?per_page=100" --jq '.[] | {tag_name, draft} | @json'
if ($LASTEXITCODE -ne 0) { throw 'Cannot query GitHub Releases. Check contents: write permission and repository access.' }
$existing = @($releaseJson | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.tag_name -ceq $Tag })
if ($existing.Count -eq 0) {
    gh release create $Tag --verify-tag --draft --title $metadata.title --notes-file $notesPath
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create draft release.' }
}
gh release upload $Tag $zipPath --clobber
if ($LASTEXITCODE -ne 0) { throw 'Asset upload failed. Re-run the workflow to retry; a new release remains draft.' }
$prerelease = if ($Tag.Split('+')[0].Contains('-')) { 'true' } else { 'false' }
gh release edit $Tag --title $metadata.title --notes-file $notesPath --draft=false "--prerelease=$prerelease"
if ($LASTEXITCODE -ne 0) { throw 'Cannot finalize release. Re-run the workflow to retry.' }
