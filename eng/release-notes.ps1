param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$ChangelogPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'changelog.md'),
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ChangelogPath -PathType Leaf)) {
    throw "Missing changelog.md. Add a '## $Tag' entry on main before pushing the version tag."
}
# Recognize actual level-two headings, ignoring headings inside fenced examples.
$entries = @()
$current = $null
$fence = $null
foreach ($line in [IO.File]::ReadAllLines((Resolve-Path -LiteralPath $ChangelogPath))) {
    if ($line -match '^ {0,3}(`{3,}|~{3,})') {
        $marker = $Matches[1]
        if (-not $fence) { $fence = $marker }
        elseif ($marker[0] -eq $fence[0] -and $marker.Length -ge $fence.Length -and $line.Trim() -eq $marker) { $fence = $null }
    }
    if (-not $fence -and $line -match '^ {0,3}##[ \t]+(.+?)[ \t]*$') {
        $title = $Matches[1] -replace '[ \t]+#+[ \t]*$', ''
        $current = @{ title = $title; lines = [Collections.Generic.List[string]]::new() }
        $entries += $current
    } elseif ($current) { $current.lines.Add($line) }
}
$matching = @($entries | Where-Object { $_.title -ieq $Tag })
if ($matching.Count -ne 1) {
    throw "Expected exactly one '## $Tag' entry in changelog.md (case-insensitive); found $($matching.Count). Add or fix that version entry on main before pushing the tag."
}
$notes = ($matching[0].lines -join "`n").Trim()
if (-not $notes) { throw "The '## $Tag' entry is empty. Add release notes in changelog.md before publishing." }
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
[IO.File]::WriteAllText([IO.Path]::GetFullPath((Join-Path $OutputDirectory 'notes.md')), $notes + "`n")
@{ tag = $Tag; title = $matching[0].title } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'metadata.json') -Encoding utf8
