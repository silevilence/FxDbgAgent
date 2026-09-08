param([ValidateSet('Debug','Release')][string]$Configuration,[string]$NodePath='C:\Program Files\nodejs\node.exe')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$report=Join-Path $repoRoot 'artifacts/stage4-validation'
$null=New-Item -ItemType Directory -Path $report -Force
if(-not ('FxDbg.Validation.ValidationProcess' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ValidationProcess.cs') }
$shell=(Get-Process -Id $PID).Path
$outcomes=[Collections.Generic.List[object]]::new()
function Inputs {
    foreach($directory in @('src','tests','eng','extensions','skills')) {
        Get-ChildItem -LiteralPath (Join-Path $repoRoot $directory) -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' -and $_.Extension -in @('.cs','.csproj','.ps1','.js','.mjs','.json','.md','.props','.targets','.config','.runsettings') } |
            Sort-Object FullName | ForEach-Object { "$([IO.Path]::GetRelativePath($repoRoot,$_.FullName)) $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }
    }
}
$inputs=@(Inputs)
$inputs | Set-Content -LiteralPath (Join-Path $report 'stage4-3-inputs.sha256')
function Run([string]$Name,[string]$Executable,[string[]]$Arguments,[int]$Seconds=300) {
    $log=Join-Path $report "$Name.log"
    $code=[FxDbg.Validation.ValidationProcess]::Run($Executable,$Arguments,$repoRoot,$log,$Seconds)
    $outcomes.Add(@{name=$Name;exitCode=$code;log=$log})
    Get-Content -LiteralPath $log -Tail 12 | Write-Host
    if($code -ne 0) { throw "$Name failed: $code ($log)" }
}
$passed=$false
try {
    foreach($current in $(if($Configuration){@($Configuration)}else{@('Debug','Release')})) {
        Run "conditional-breakpoints-publish-mcp-$current" $shell @('-NoProfile','-File',(Join-Path $PSScriptRoot 'publish-mcp.ps1'),'-Configuration',$current) 600
        Run "conditional-breakpoints-publish-dap-$current" $shell @('-NoProfile','-File',(Join-Path $PSScriptRoot 'publish-dap.ps1'),'-Configuration',$current) 600
        Run "conditional-breakpoints-unit-$current" 'dotnet' @('test','tests/FxDbg.UnitTests/FxDbg.UnitTests.csproj','-c',$current,'--no-build','--no-restore') 180
        foreach($architecture in @('x86','x64')) {
            Run "conditional-breakpoints-native-$current-$architecture" (Join-Path $repoRoot "tests/FxDbg.IntegrationTests/bin/$current/net48/FxDbg.IntegrationTests.$architecture.exe") @($repoRoot,$current,'conditional-breakpoints') 120
        }
        Run "conditional-breakpoints-mcp-$current" 'dotnet' @((Join-Path $repoRoot "tests/FxDbg.McpIntegrationTests/bin/$current/net10.0-windows/FxDbg.McpIntegrationTests.dll"),(Join-Path $repoRoot "artifacts/mcp/$current"),'conditional-breakpoints',$repoRoot,$current) 180
        Run "conditional-breakpoints-dap-cli-$current" $NodePath @((Join-Path $repoRoot 'tests/FxDbg.DapTests/conditional-breakpoints.js'),(Join-Path $repoRoot "artifacts/dap/$current"),$repoRoot,$current) 600
    }
    if(Compare-Object $inputs @(Inputs)) { throw 'Validation inputs changed; rerun the complete matrix.' }
    $passed=$true
} finally {
    @{passed=$passed;atUtc=[DateTimeOffset]::UtcNow.ToString('o');configuration=$(if($Configuration){$Configuration}else{'Debug+Release'});outcomes=@($outcomes.ToArray())} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $report 'stage4-3-result.json')
}
