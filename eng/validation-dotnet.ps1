# Deliberately use $args: preserve native dotnet switches without PowerShell parameter binding.
$ErrorActionPreference = 'Stop'
$dotnetArguments = @($args)
. (Join-Path $PSScriptRoot 'validation-reuse.ps1')
Invoke-ValidationDotNet -Arguments $dotnetArguments
