param([string]$ProjectDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$reportDirectory = Join-Path $repoRoot 'artifacts/stage2-validation'
if (-not $ProjectDirectory) { $ProjectDirectory = Join-Path $reportDirectory ('skill-project-' + [Guid]::NewGuid().ToString('N')) }
$ProjectDirectory = [IO.Path]::GetFullPath($ProjectDirectory)
if (-not $ProjectDirectory.StartsWith([IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Skill validation must use an artifacts project directory.'
}
$null = New-Item -ItemType Directory -Path $ProjectDirectory -Force
$env:npm_config_cache = Join-Path $repoRoot 'artifacts/npm-cache'
$env:DISABLE_TELEMETRY = '1'
$env:DO_NOT_TRACK = '1'
$env:npm_config_fetch_retries = '0'
$env:npm_config_fetch_timeout = '15000'
Push-Location $ProjectDirectory
try {
    npx --yes skills@1.5.23 add $repoRoot --skill fxdbg-agent --agent codex --yes
    if ($LASTEXITCODE -ne 0) { throw 'Project-only skills installation failed.' }
}
finally { Pop-Location }
$installed = Join-Path $ProjectDirectory '.agents/skills/fxdbg-agent'
$evidence = @()
foreach ($relative in @('SKILL.md','references/workflow.md','references/mcp-tools.md')) {
    $source = Join-Path $repoRoot "skills/fxdbg-agent/$relative"
    $destination = Join-Path $installed $relative
    if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw "Installed file missing: $relative" }
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $sourceHash) { throw "Installed content differs: $relative" }
    $evidence += @{ file = $relative; sha256 = $sourceHash }
}
foreach ($pair in @(@('docs/agent-skill.md','skills/fxdbg-agent/references/workflow.md'),@('docs/mcp-tools.md','skills/fxdbg-agent/references/mcp-tools.md'))) {
    if ((Get-FileHash -LiteralPath (Join-Path $repoRoot $pair[0])).Hash -ne (Get-FileHash -LiteralPath (Join-Path $repoRoot $pair[1])).Hash) { throw 'Packaged references are out of sync with docs.' }
}
$frontmatter = Get-Content -Raw -LiteralPath (Join-Path $installed 'SKILL.md')
if ($frontmatter -notmatch '(?m)^name: fxdbg-agent\r?$' -or $frontmatter -notmatch '(?m)^description: .+$') { throw 'Skill frontmatter is invalid.' }
@{ cliVersion = '1.5.23'; command = "npx --yes skills@1.5.23 add $repoRoot --skill fxdbg-agent --agent codex --yes"; projectDirectory = $ProjectDirectory; installedDirectory = $installed; files = $evidence; validatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o') } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportDirectory 'skill-install.json') -Encoding utf8
Write-Host "Skill installed and references verified: $installed"
