param([ValidateSet('Debug','Release')][string]$Configuration, [string]$NodePath = 'node', [string]$CodePath = 'code')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$report = Join-Path $repoRoot 'artifacts/stage3-validation'
$null = New-Item -ItemType Directory -Path $report -Force
if (-not ('FxDbg.Validation.ValidationProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ValidationProcess.cs') }
$shell = (Get-Process -Id $PID).Path
$env:FXDBG_DOTNET = (Get-Command dotnet).Source
$env:FXDBG_TEST_ROOT = $repoRoot
$configurations = if ($Configuration) { @($Configuration) } else { @('Debug','Release') }
foreach ($current in $configurations) {
    $bundle = Join-Path $repoRoot "artifacts/dap/$current"
    $env:FXDBG_TEST_CONFIGURATION = $current
    $env:FXDBG_TEST_BUNDLE = $bundle
    $code = [FxDbg.Validation.ValidationProcess]::Run($shell,
        @('-NoProfile','-File',(Join-Path $PSScriptRoot 'publish-dap.ps1'),'-Configuration',$current),
        $repoRoot, (Join-Path $report "dap-build-$current.log"), 600)
    if ($code -ne 0) { throw "DAP build failed: $current ($code)" }
    $code = [FxDbg.Validation.ValidationProcess]::Run($NodePath,
        @((Join-Path $repoRoot 'tests/FxDbg.DapTests/protocol.js'),$bundle,$repoRoot,$current),
        $repoRoot, (Join-Path $report "dap-protocol-$current.log"), 300)
    Get-Content (Join-Path $report "dap-protocol-$current.log") | Write-Host
    if ($code -ne 0) { throw "DAP protocol tests failed: $current ($code)" }
    $clientDirectory = Join-Path $report ('vscode-profile-' + [Guid]::NewGuid().ToString('N'))
    $code = [FxDbg.Validation.ValidationProcess]::Run($CodePath,
        @('--disable-extensions','--disable-updates','--skip-welcome','--skip-release-notes','--disable-workspace-trust',
          '--user-data-dir',(Join-Path $clientDirectory 'data'),'--extensions-dir',(Join-Path $clientDirectory 'extensions'),
          "--extensionDevelopmentPath=$(Join-Path $repoRoot 'extensions/fxdbg')", "--extensionTestsPath=$(Join-Path $repoRoot 'tests/FxDbg.DapTests/vscode.js')"),
        $repoRoot, (Join-Path $report "dap-vscode-$current.log"), 300)
    Get-Content (Join-Path $report "dap-vscode-$current.log") | Write-Host
    if ($code -ne 0) { throw "VS Code tests failed: $current ($code)" }
    $evidence = Get-Content (Join-Path $report "vscode-$current.json") -Raw | ConvertFrom-Json
    if (-not $evidence.passed -or $evidence.cases.Count -ne 4) { throw 'Real VS Code evidence is incomplete.' }
}
