[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$env:DOTNET_CLI_HOME = Join-Path $artifactsRoot 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $artifactsRoot 'nuget-packages'

Push-Location $repositoryRoot
try {
    & dotnet build 'FxDbg.sln' --configuration $Configuration --no-incremental
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    $projectNames = @(
        'Fx40.Console.x86',
        'Fx40.Console.x64',
        'Fx40.WinForms.x86',
        'Fx40.WinForms.x64'
    )

    foreach ($projectName in $projectNames) {
        $outputDirectory = Join-Path $repositoryRoot "tests/Debuggees/$projectName/bin/$Configuration/net40"
        $executablePath = Join-Path $outputDirectory "$projectName.exe"
        $pdbPath = Join-Path $outputDirectory "$projectName.pdb"

        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw "Missing debuggee executable: $executablePath"
        }

        if (-not (Test-Path -LiteralPath $pdbPath -PathType Leaf)) {
            throw "Missing debuggee PDB: $pdbPath"
        }

        $stream = [System.IO.File]::OpenRead($pdbPath)
        try {
            $headerBytes = New-Object byte[] 24
            $bytesRead = $stream.Read($headerBytes, 0, $headerBytes.Length)
        }
        finally {
            $stream.Dispose()
        }

        $header = [System.Text.Encoding]::ASCII.GetString($headerBytes, 0, $bytesRead)
        if ($header -ne 'Microsoft C/C++ MSF 7.00') {
            throw "PDB is not Windows PDB (MSF 7.00): $pdbPath (header: '$header')"
        }
    }

    foreach ($architecture in @('x86', 'x64')) {
        $projectName = "Fx40.WinForms.$architecture"
        $executablePath = Join-Path $repositoryRoot "tests/Debuggees/$projectName/bin/$Configuration/net40/$projectName.exe"
        $process = Start-Process -FilePath $executablePath -PassThru
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            $windowHandle = [IntPtr]::Zero
            while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
                $process.Refresh()
                $windowHandle = $process.MainWindowHandle
                if ($windowHandle -ne [IntPtr]::Zero) {
                    break
                }
                Start-Sleep -Milliseconds 100
            }

            if ($windowHandle -eq [IntPtr]::Zero) {
                throw "$projectName did not display a main window within 10 seconds."
            }

            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) {
                throw "$projectName did not exit after its main window was closed."
            }
        }
        finally {
            if (-not $process.HasExited) {
                $process.Kill()
                [void]$process.WaitForExit(5000)
            }
            $process.Dispose()
        }
    }

    Write-Host 'Stage 0-1 verification passed: all debuggees built, all PDBs are MSF 7.00, and both WinForms windows opened.'
}
finally {
    Pop-Location
}
