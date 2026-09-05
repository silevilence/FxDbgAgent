param([string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $EvidenceDirectory) { $EvidenceDirectory = Join-Path $repoRoot 'docs/validation/stage2-3-agent-evidence' }
foreach ($file in @('result.json','calls.jsonl','decisions.md','agent-run.json','ready.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $EvidenceDirectory $file) -PathType Leaf)) { throw "Independent Agent evidence missing: $file" }
}
$result = Get-Content -Raw -LiteralPath (Join-Path $EvidenceDirectory 'result.json') | ConvertFrom-Json
if ($result.passed -ne $true) { throw 'Independent Agent did not mark acceptance passed.' }
$journal = @(Get-Content -LiteralPath (Join-Path $EvidenceDirectory 'calls.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
$calls = @($journal | Where-Object { $_.request.kind -eq 'tools/call' })
foreach ($tool in @('debug_launch','debug_set_breakpoint','debug_continue','debug_threads','debug_stack','debug_variables','debug_step','debug_remove_breakpoint','debug_detach','debug_status')) {
    if ($tool -notin $calls.request.name) { throw "Independent Agent never called $tool" }
}
$responses = @($journal | Where-Object response)
if (-not ($responses | Where-Object { $_.response.structuredContent.error.code -eq 'frame_not_found' })) { throw 'Missing real structured-error recovery evidence.' }
if (-not ($responses | Where-Object { $_.response.structuredContent.result.stop.reason -eq 'breakpoint' })) { throw 'Missing real breakpoint observation.' }
if (-not ($responses | Where-Object { $_.response.structuredContent.result.stop.reason -eq 'step' })) { throw 'Missing real step observation.' }
if (-not ($journal | Where-Object { $_.request.kind -eq 'close' })) { throw 'Agent did not close its bridge.' }
$names = @($calls.request.name | Sort-Object -Unique)
Write-Host "Independent Agent evidence passed: $($calls.Count) calls, $($names.Count) tools, explicit error recovery and cleanup."
