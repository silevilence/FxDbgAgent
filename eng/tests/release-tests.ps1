# Local regression checks for release automation; no GitHub writes or debuggee execution.
param([string]$BundleDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$work = Join-Path $root ('artifacts/release-tests/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work -Force
$passed = 0
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}
function Expect-Failure([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch {
        Assert ($_.Exception.Message -match $Pattern) "Unexpected error: $_"
        return
    }
    throw "Expected error matching $Pattern"
}

# Execute the exact inline eligibility gate against an isolated Git history.
$workflow = Get-Content -LiteralPath (Join-Path $root '.github/workflows/release.yml') -Raw
$gate = [regex]::Match($workflow, '(?m)^        run: \|\r?\n(?<body>(?:          [^\r\n]*\r?\n|\r?\n)+)').Groups['body'].Value
Assert (-not [string]::IsNullOrWhiteSpace($gate)) 'Cannot locate inline gate'
$gatePath = Join-Path $work 'gate.ps1'
($gate -replace '(?m)^          ', '') | Set-Content -LiteralPath $gatePath
$repo = Join-Path $work 'repo'
git init --quiet --initial-branch=main $repo
if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize test repository' }
$oldTag = $env:RELEASE_TAG
$oldOutput = $env:GITHUB_OUTPUT
Push-Location $repo
try {
    git -c user.name=ReleaseTests -c user.email=tests@example.invalid commit --quiet --allow-empty -m base
    git update-ref refs/remotes/origin/main HEAD
    function Check-Gate([string]$Tag, [string]$Expected) {
        $env:RELEASE_TAG = $Tag
        $env:GITHUB_OUTPUT = Join-Path $work ([Guid]::NewGuid().ToString('N') + '.output')
        & (Get-Process -Id $PID).Path -NoProfile -File $gatePath | Out-Null
        Assert ($LASTEXITCODE -eq 0) "Gate failed for $Tag"
        Assert ((Get-Content -LiteralPath $env:GITHUB_OUTPUT -Raw).Trim() -eq "eligible=$Expected") "Wrong eligibility for $Tag"
    }
    foreach ($tag in @('V0.1.0','v0.1.0','V12.34.56','v1.0.0-rc.1','v1.0.0+build.01','v1.0.0-rc.1+build.4')) { Check-Gate $tag true }
    foreach ($tag in @('foo','1.0.0','v1.2','V01.2.3','v1.2.3.4','v1.0.0-01','v1.0.0-','v1.0.0+','v1.0.0-rc..1','v1.0.0/x')) { Check-Gate $tag false }
    git -c user.name=ReleaseTests -c user.email=tests@example.invalid commit --quiet --allow-empty -m outside-main
    Check-Gate V0.1.0 false
    git update-ref refs/remotes/origin/main HEAD
    git checkout --quiet --detach HEAD~1
    Check-Gate V0.1.0 true
} finally {
    Pop-Location
    $env:RELEASE_TAG = $oldTag
    $env:GITHUB_OUTPUT = $oldOutput
}

$changelog = Join-Path $work 'changelog.md'
$notesOutput = Join-Path $work 'notes'
@'
# Change Log
## V0.2.0
Next
## V0.1.0
### Features
Literal `$value` and $(example), Unicode: 发布。
```markdown
## V9.9.9
```
## V0.0.1
Old
'@ | Set-Content -LiteralPath $changelog
& (Join-Path $root 'eng/release-notes.ps1') -Tag v0.1.0 -ChangelogPath $changelog -OutputDirectory $notesOutput
$metadata = Get-Content (Join-Path $notesOutput 'metadata.json') -Raw | ConvertFrom-Json
$notes = Get-Content (Join-Path $notesOutput 'notes.md') -Raw
Assert ($metadata.title -ceq 'V0.1.0' -and $metadata.tag -ceq 'v0.1.0') 'Case-preserving metadata mismatch'
Assert ($notes.Contains('$(example)') -and $notes.Contains('## V9.9.9') -and -not $notes.Contains('Old') -and -not $notes.Contains('Next')) 'Incorrect changelog block'
Expect-Failure { & "$root/eng/release-notes.ps1" -Tag V9.9.9 -ChangelogPath $changelog -OutputDirectory $notesOutput } 'found 0'
Add-Content -LiteralPath $changelog -Value "`n## v0.1.0`nDuplicate"
Expect-Failure { & "$root/eng/release-notes.ps1" -Tag V0.1.0 -ChangelogPath $changelog -OutputDirectory $notesOutput } 'found 2'
Set-Content -LiteralPath $changelog -Value '## V0.1.0'
Expect-Failure { & "$root/eng/release-notes.ps1" -Tag V0.1.0 -ChangelogPath $changelog -OutputDirectory $notesOutput } 'empty'
Expect-Failure { & "$root/eng/release-notes.ps1" -Tag V0.1.0 -ChangelogPath "$work/missing.md" -OutputDirectory $notesOutput } 'Missing changelog'

# Mock only the external GitHub boundary; exercise real create/update/failure sequencing.
Set-Content -LiteralPath (Join-Path $notesOutput 'fxdbg-0.1.0.zip') -Value fixture
$mock = @{ calls = [Collections.Generic.List[string]]::new(); existing = $false; failCommand = '' }
$global:FxDbgReleaseTestGh = $mock
function gh {
    $command = $args[0..1] -join ' '
    $global:FxDbgReleaseTestGh.calls.Add(($args -join ' '))
    $global:LASTEXITCODE = if ($command -eq $global:FxDbgReleaseTestGh.failCommand) { 1 } else { 0 }
    if ($args[0] -eq 'api' -and $global:FxDbgReleaseTestGh.existing) { '{"tag_name":"v0.1.0","draft":false}' }
}
& "$root/eng/release-github.ps1" -Tag v0.1.0 -OutputDirectory $notesOutput
Assert ($mock.calls.Count -eq 4 -and $mock.calls[1].Contains('--draft') -and $mock.calls[2].Contains('--clobber') -and $mock.calls[3].Contains('--draft=false')) 'Wrong create/upload/finalize order'
$mock.existing = $true
$mock.calls.Clear()
& "$root/eng/release-github.ps1" -Tag v0.1.0 -OutputDirectory $notesOutput
Assert ($mock.calls.Count -eq 3 -and -not ($mock.calls -match 'release create')) 'Retry must update existing release'
$mock.failCommand = 'release upload'
$mock.calls.Clear()
Expect-Failure { & "$root/eng/release-github.ps1" -Tag v0.1.0 -OutputDirectory $notesOutput } 'Asset upload failed'
Assert (-not ($mock.calls -match 'release edit')) 'Failed upload must not finalize'
$mock.failCommand = 'api --paginate'
$mock.calls.Clear()
Expect-Failure { & "$root/eng/release-github.ps1" -Tag v0.1.0 -OutputDirectory $notesOutput } 'Cannot query'
Assert ($mock.calls.Count -eq 1) 'API failure must not create a release'
Remove-Item Function:gh
Remove-Variable FxDbgReleaseTestGh -Scope Global

if ($BundleDirectory) {
    $bundle = [IO.Path]::GetFullPath($BundleDirectory)
    & "$root/eng/package-release.ps1" -Tag V0.1.0 -BundleDirectory $bundle -OutputDirectory $work
    Assert (Test-Path -LiteralPath "$work/fxdbg-0.1.0.zip") 'ZIP not created'
    $copy = Join-Path $work 'damaged-bundle'
    Copy-Item -LiteralPath $bundle -Destination $copy -Recurse
    Add-Content -LiteralPath "$copy/fxdbg-dap.dll" -Value tampered
    Expect-Failure { & "$root/eng/package-release.ps1" -Tag V0.1.1 -BundleDirectory $copy -OutputDirectory $work } 'SHA256 mismatch'
    Remove-Item -LiteralPath "$copy/fxdbg-dap.pdb"
    Expect-Failure { & "$root/eng/package-release.ps1" -Tag V0.1.1 -BundleDirectory $copy -OutputDirectory $work } 'missing fxdbg-dap.pdb'
}
Write-Host "PASS: $passed release regression assertions. Evidence: $work"
