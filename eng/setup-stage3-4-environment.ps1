param(
    [ValidateSet('Preflight','Install','Verify','Remove')][string]$Action = 'Verify',
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [ValidatePattern('^Stage3-4(-[a-z0-9]{1,20})?$')][string]$EnvironmentName = 'Stage3-4',
    [ValidateRange(1024,65534)][int]$PortBase = 58340,
    [ValidateSet('None','AfterFirstSite')][string]$InterruptAt = 'None'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$suffix = $EnvironmentName.Substring('Stage3-4'.Length)
$namePrefix = "FxDbgStage34$suffix"
$evidence = Join-Path $repoRoot "artifacts/stage3-4-environment$suffix"
$statePath = Join-Path $evidence 'state.json'
$deploymentRoot = Join-Path $env:ProgramData "FxDbgAgent/$EnvironmentName"
$admin = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if ($Action -ne 'Preflight' -and -not $admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell. It does not elevate itself.'
}
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell.' }
$appcmd = Join-Path $env:windir 'System32/inetsrv/appcmd.exe'
$requiredFeatures = @('IIS-WebServerRole','IIS-WebServer','IIS-StaticContent','IIS-DefaultDocument',
    'IIS-HttpErrors','IIS-HttpLogging','IIS-RequestFiltering','IIS-NetFxExtensibility45',
    'IIS-ASPNET45','IIS-ISAPIExtensions','IIS-ISAPIFilter','IIS-ManagementConsole')
$state = $null

if ($Action -eq 'Preflight') {
    $elevated = $admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $missingFeatures = if ($elevated) {
        $available = @(Get-WindowsOptionalFeature -Online)
        @($requiredFeatures | Where-Object { $featureName=$_; -not ($available | Where-Object { $_.FeatureName -eq $featureName -and $_.State -eq 'Enabled' }) })
    } else { @('unknown: component inspection needs an elevated token') }
    $clr = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
    $ports = @(Get-NetTCPConnection -LocalPort $PortBase,($PortBase+1) -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty LocalPort -Unique)
    [pscustomobject]@{ elevated=$elevated; os=[Environment]::OSVersion.Version.ToString(); x64=[Environment]::Is64BitOperatingSystem;
        frameworkRelease=$(if($clr) {$clr.Release} else {$null}); appcmdExists=(Test-Path -LiteralPath $appcmd);
        missingFeatures=$missingFeatures; occupiedPorts=$ports; deploymentExists=(Test-Path -LiteralPath $deploymentRoot);
        deploymentRoot=$deploymentRoot; names=@("$namePrefix-x86","$namePrefix-x64") } | ConvertTo-Json -Depth 4
    return
}
$null = New-Item -ItemType Directory -Path $evidence -Force

function Save-State {
    $temporary = "$statePath.tmp"
    [IO.File]::WriteAllText($temporary, ($state | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $statePath, $true)
}
function Invoke-AppCmd([string[]]$Arguments) {
    $output = & $appcmd @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "AppCmd failed ($LASTEXITCODE): $output" }
    $output | Write-Host
}
function Invoke-Acl([string]$Path, [string]$Grant) {
    $output = & icacls.exe $Path /grant $Grant 2>&1
    if ($LASTEXITCODE -ne 0) { throw "ACL update failed: $output" }
}
function Grant-PoolRead([string]$Path, [string]$Pool) {
    # WAS account-name publication can lag applicationHost.config CommitChanges.
    $sid = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try { $sid = [Security.Principal.NTAccount]::new("IIS AppPool\$Pool").Translate([Security.Principal.SecurityIdentifier]); break }
        catch [Security.Principal.IdentityNotMappedException] { Start-Sleep -Milliseconds 500 }
    }
    if (-not $sid) { throw "Application pool identity did not become available: $Pool" }
    Invoke-Acl $Path ("*{0}:(OI)(CI)(RX)" -f $sid.Value)
}
function Assert-OwnedRoot {
    $resolved = [IO.Path]::GetFullPath($state.deploymentRoot).TrimEnd('\')
    $expected = [IO.Path]::GetFullPath($deploymentRoot).TrimEnd('\')
    if ($resolved -ne $expected) { throw 'Manifest deployment root does not match the dedicated test directory.' }
    $marker = Join-Path $resolved '.fxdbg-environment-id'
    if (-not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne $state.id) {
        throw 'Deployment ownership marker is missing or mismatched.'
    }
    if (@(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object {
        $_.Attributes -band [IO.FileAttributes]::ReparsePoint
    }).Count -or ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing file operations on a deployment containing reparse points.'
    }
}
function Get-ServiceRecord([string]$Name) {
    if ($Name -notin @("$namePrefix-x86","$namePrefix-x64")) { throw 'Unexpected test service name.' }
    Get-CimInstance Win32_Service -Filter "Name='$Name'"
}
function Read-Heartbeat([string]$Path) {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
        catch { Start-Sleep -Milliseconds 250 }
    }
    throw "No valid heartbeat: $Path"
}
function Test-Environment {
    Assert-OwnedRoot
    $results = @()
    foreach ($resource in $state.resources) {
        $service = Get-ServiceRecord $resource.service
        if (-not $service -or $service.State -ne 'Running' -or $service.StartName -ne 'NT AUTHORITY\LocalService') {
            throw "Service is not running under LocalService: $($resource.service)"
        }
        if ($service.PathName -ne $resource.binaryPath) { throw 'Service executable differs from the owned deployment.' }
        $first = Read-Heartbeat $resource.heartbeat
        Start-Sleep -Milliseconds 1500
        $second = Read-Heartbeat $resource.heartbeat
        if ($second.sequence -le $first.sequence -or $second.pid -ne $service.ProcessId -or
            $second.bits -ne $resource.bits -or $second.sessionId -ne 0 -or $second.runtime -notlike '4.0.*') {
            throw "Invalid service heartbeat: $($resource.service)"
        }
        $health = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        do {
            try { $health = Invoke-RestMethod -Uri $resource.url -TimeoutSec 10 -UseBasicParsing }
            catch { $lastHttpError = $_.Exception.Message; Start-Sleep -Milliseconds 500 }
        } until ($health -or [DateTime]::UtcNow -gt $deadline)
        if (-not $health) { throw "IIS did not become healthy: $lastHttpError" }
        if ($health.kind -ne 'FxDbg-stage3-4-environment' -or $health.bits -ne $resource.bits -or
            $health.runtime -notlike '4.0.*' -or $health.result -ne 85 -or -not $health.shadowCopy -or
            $health.assemblyPath.StartsWith($resource.webRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "IIS health/architecture/shadow-copy check failed: $($resource.site)"
        }
        $workers = & $appcmd list wp "/apppool.name:$($resource.pool)" /text:WP.NAME
        if ($LASTEXITCODE -ne 0 -or @($workers) -notcontains [string]$health.pid) { throw 'HTTP PID is not in the expected application pool.' }
        $results += [pscustomobject]@{ architecture=$resource.architecture; service=$resource.service;
            serviceStartedAtUtc=(Get-Process -Id $service.ProcessId).StartTime.ToUniversalTime().ToString('o');
            workerStartedAtUtc=(Get-Process -Id $health.pid).StartTime.ToUniversalTime().ToString('o');
            serviceHealth=$second; site=$resource.site; url=$resource.url; webHealth=$health }
    }
    $state.status = 'ready'
    $state.error = $null
    $state.verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Save-State
    $results | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'health.json') -Encoding UTF8
    Write-Host 'Both Service architectures and both IIS workers are healthy, with CLR v4 and IIS shadow copying.'
}

$transcript = Join-Path $evidence ("{0}-{1}.log" -f $Action, (Get-Date -Format 'yyyyMMdd-HHmmss'))
Start-Transcript -LiteralPath $transcript | Out-Null
try {
    if ($Action -ne 'Install') {
        if (-not (Test-Path -LiteralPath $statePath)) { throw 'No deployment manifest exists.' }
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        # Upgrade only manifests from the already-owned initial environment installation.
        if (-not $state.PSObject.Properties['ownedServices']) {
            Assert-OwnedRoot
            $state | Add-Member -NotePropertyName ownedServices -NotePropertyValue @($state.resources.service)
            $state | Add-Member -NotePropertyName ownedSites -NotePropertyValue @($state.resources.site)
            $state | Add-Member -NotePropertyName ownedPools -NotePropertyValue @($state.resources.pool)
        }
        if ($Action -eq 'Verify') { Test-Environment }
        else {
            if (Test-Path -LiteralPath $deploymentRoot) { Assert-OwnedRoot }
            elseif ($state.ownedServices.Count -or $state.ownedSites.Count -or $state.ownedPools.Count) { throw 'Owned resources exist but their directory marker is missing.' }
            Add-Type -Path (Join-Path $env:windir 'System32/inetsrv/Microsoft.Web.Administration.dll')
            $manager = [Microsoft.Web.Administration.ServerManager]::new()
            try {
                foreach ($resource in $state.resources) {
                    if ($resource.site -notin @("$namePrefix-x86","$namePrefix-x64") -or $resource.pool -ne $resource.site) { throw 'Unexpected IIS resource name.' }
                    if ($resource.site -notin $state.ownedSites) { continue }
                    $site = $manager.Sites[$resource.site]
                    if ($site) {
                        if ($site.Applications['/'].VirtualDirectories['/'].PhysicalPath -ne $resource.webRoot) { throw 'Site no longer belongs to this deployment.' }
                        $manager.Sites.Remove($site)
                    }
                    $iisConfiguration = $manager.GetApplicationHostConfiguration()
                    foreach ($location in @($iisConfiguration.GetLocationPaths() | Where-Object {
                        $_ -eq $resource.site -or $_.StartsWith($resource.site+'/',[StringComparison]::OrdinalIgnoreCase)
                    })) { $iisConfiguration.RemoveLocationPath($location) }
                }
                $manager.CommitChanges()
                foreach ($resource in $state.resources) {
                    if ($resource.pool -notin $state.ownedPools) { continue }
                    $pool = $manager.ApplicationPools[$resource.pool]
                    if ($pool) {
                        foreach ($site in $manager.Sites) { foreach ($application in $site.Applications) {
                            if ($application.ApplicationPoolName -eq $resource.pool) { throw 'Another site now uses the test pool.' }
                        } }
                        $manager.ApplicationPools.Remove($pool)
                    }
                }
                $manager.CommitChanges()
            } finally { $manager.Dispose() }
            foreach ($resource in $state.resources) {
                if ($resource.service -notin $state.ownedServices) { continue }
                $service = Get-ServiceRecord $resource.service
                if ($service) {
                    if ($service.PathName -ne $resource.binaryPath) { throw 'Service path changed; refusing removal.' }
                    $controller = Get-Service -Name $resource.service
                    if ($controller.Status -ne 'Stopped') {
                        $controller.Stop()
                        $controller.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
                    }
                    $controller.Dispose()
                    & sc.exe delete $resource.service
                    if ($LASTEXITCODE -ne 0) { throw 'Service removal failed.' }
                }
            }
            if (Test-Path -LiteralPath $deploymentRoot) {
                Assert-OwnedRoot
                Remove-Item -LiteralPath ([IO.Path]::GetFullPath($deploymentRoot)) -Recurse -Force
            }
            $state.status = 'removed'
            Save-State
            Write-Host 'Removed owned services, sites, pools and deployment files. Windows components are retained; see features-before/after.json.'
        }
    }
    else {
        if (Test-Path -LiteralPath $deploymentRoot) { throw "Deployment directory already exists. Use Verify or Remove first: $deploymentRoot" }
        if (Test-Path -LiteralPath $statePath) {
            $previous = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
            if ($previous.status -ne 'removed') { throw 'An earlier deployment is recorded; inspect its manifest before retrying.' }
            Copy-Item -LiteralPath $statePath -Destination (Join-Path $evidence ("state-{0}.json" -f $previous.id))
            foreach ($file in @('features-before.json','features-after.json','health.json')) {
                $oldEvidence = Join-Path $evidence $file
                if (Test-Path -LiteralPath $oldEvidence) {
                    Copy-Item -LiteralPath $oldEvidence -Destination (Join-Path $evidence ("{0}-{1}" -f $previous.id,$file))
                }
            }
        }
        $resources = @()
        foreach ($arch in @('x86','x64')) {
            $name = "$namePrefix-$arch"
            $port = if ($arch -eq 'x86') { $PortBase } else { $PortBase+1 }
            if (Get-ServiceRecord $name) { throw "Service name already exists: $name" }
            if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { throw "Port $port is occupied." }
            $serviceRoot = Join-Path $deploymentRoot "service-$arch"
            $webRoot = Join-Path $deploymentRoot "web-$arch"
            $heartbeat = Join-Path $serviceRoot 'data/heartbeat.json'
            $exe = Join-Path $serviceRoot "Fx40.Environment.Service.$arch.exe"
            $resources += [pscustomobject]@{ architecture=$arch; bits=$(if ($arch -eq 'x86') {32} else {64});
                service=$name; pool=$name; site=$name; port=$port; webRoot=$webRoot; serviceRoot=$serviceRoot;
                heartbeat=$heartbeat; binaryPath=('"{0}" "{1}" "{2}"' -f $exe,$name,$heartbeat);
                url="http://127.0.0.1:$port/health.aspx" }
        }
        foreach ($project in @('Fx40.Environment.Service.x86','Fx40.Environment.Service.x64','Fx40.Environment.Web','Fx40.Environment.Late')) {
            $pdb = Join-Path $repoRoot "tests/Debuggees/$project/bin/$Configuration/net40/$project.pdb"
            if (-not (Test-Path -LiteralPath $pdb) -or [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($pdb),0,24) -notlike 'Microsoft C/C++ MSF 7.00*') {
                throw "Build the $Configuration sample with Windows PDB first: $project"
            }
        }
        $features = @(Get-WindowsOptionalFeature -Online)
        $features | Select-Object FeatureName,@{n='State';e={$_.State.ToString()}} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'features-before.json') -Encoding UTF8
        $iisPreviouslyEnabled = @($features | Where-Object { $_.FeatureName -eq 'IIS-WebServer' -and $_.State -eq 'Enabled' }).Count -gt 0
        $state = [pscustomobject]@{ id=[guid]::NewGuid().ToString(); status='installing'; deploymentRoot=$deploymentRoot;
            configuration=$Configuration; installedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); verifiedAtUtc=$null;
            resources=$resources; ownedServices=@(); ownedSites=@(); ownedPools=@();
            restartNeeded=$false; addedFeatures=@(); error=$null; iisPreviouslyEnabled=$iisPreviouslyEnabled }
        Save-State
        $missing = @($requiredFeatures | Where-Object { $name=$_; -not ($features | Where-Object { $_.FeatureName -eq $name -and $_.State -eq 'Enabled' }) })
        if ($missing.Count) {
            Write-Host "Enabling required IIS/ASP.NET components: $($missing -join ', ')"
            $enabled = Enable-WindowsOptionalFeature -Online -FeatureName $missing -All -NoRestart
            $state.restartNeeded = [bool]$enabled.RestartNeeded
        }
        $after = @(Get-WindowsOptionalFeature -Online)
        $after | Select-Object FeatureName,@{n='State';e={$_.State.ToString()}} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'features-after.json') -Encoding UTF8
        $state.addedFeatures = @($after | Where-Object {
            $name=$_.FeatureName
            $_.State -eq 'Enabled' -and -not ($features | Where-Object { $_.FeatureName -eq $name -and $_.State -eq 'Enabled' })
        } | Select-Object -ExpandProperty FeatureName)
        Save-State
        Add-Type -Path (Join-Path $env:windir 'System32/inetsrv/Microsoft.Web.Administration.dll')
        $manager = [Microsoft.Web.Administration.ServerManager]::new()
        try {
            # A newly enabled IIS creates a wildcard default site; leave it disabled.
            if (-not $iisPreviouslyEnabled -and $manager.Sites['Default Web Site']) {
                $defaultSite = $manager.Sites['Default Web Site']
                $defaultSite.ServerAutoStart = $false
                if ($defaultSite.State -eq 'Started') { $null = $defaultSite.Stop() }
                $manager.CommitChanges()
            }
            if ($state.restartNeeded) { $state.status='restartRequired'; Save-State; throw 'Windows requires a restart. No automatic restart was performed.' }
            foreach ($resource in $resources) {
                if ($manager.Sites[$resource.site] -or $manager.ApplicationPools[$resource.pool]) { throw 'An IIS resource name is already in use.' }
                if (@($manager.GetApplicationHostConfiguration().GetLocationPaths() | Where-Object {
                    $_ -eq $resource.site -or $_.StartsWith($resource.site+'/',[StringComparison]::OrdinalIgnoreCase)
                }).Count) { throw 'An IIS configuration location already uses the test name.' }
            }
            $null = New-Item -ItemType Directory -Path $deploymentRoot -Force
            $rootAcl = [Security.AccessControl.DirectorySecurity]::new()
            $rootAcl.SetAccessRuleProtection($true,$false)
            foreach ($entry in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-32-545','ReadAndExecute'))) {
                $rule = [Security.AccessControl.FileSystemAccessRule]::new(
                    [Security.Principal.SecurityIdentifier]::new($entry[0]),
                    [Security.AccessControl.FileSystemRights]$entry[1],
                    [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',
                    [Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)
                $rootAcl.AddAccessRule($rule)
            }
            Set-Acl -LiteralPath $deploymentRoot -AclObject $rootAcl
            $state.id | Set-Content -LiteralPath (Join-Path $deploymentRoot '.fxdbg-environment-id') -Encoding ASCII
            foreach ($resource in $resources) {
                $data = Join-Path $resource.serviceRoot 'data'
                $bin = Join-Path $resource.webRoot 'bin'
                $null = New-Item -ItemType Directory -Path $data,$bin -Force
                $project = "Fx40.Environment.Service.$($resource.architecture)"
                Get-ChildItem -LiteralPath (Join-Path $repoRoot "tests/Debuggees/$project/bin/$Configuration/net40") -File |
                    Copy-Item -Destination $resource.serviceRoot
                Get-ChildItem -LiteralPath (Join-Path $repoRoot "tests/Debuggees/Fx40.Environment.Web/bin/$Configuration/net40") -File |
                    Copy-Item -Destination $bin
                foreach ($file in @('health.aspx','web.config')) {
                    Copy-Item -LiteralPath (Join-Path $repoRoot "tests/Debuggees/Fx40.Environment.Web/$file") -Destination $resource.webRoot
                }
                foreach ($targetRoot in @($resource.serviceRoot,$resource.webRoot)) {
                    $lateDirectory = Join-Path $targetRoot 'late'
                    $null = New-Item -ItemType Directory -Path $lateDirectory
                    Get-ChildItem -LiteralPath (Join-Path $repoRoot "tests/Debuggees/Fx40.Environment.Late/bin/$Configuration/net40") -File |
                        Copy-Item -Destination $lateDirectory
                }
                Invoke-Acl $resource.serviceRoot '*S-1-5-19:(OI)(CI)(RX)'
                Invoke-Acl $data '*S-1-5-19:(OI)(CI)(M)'
                # Persist intended ownership after collision checks, before each externally visible mutation.
                $state.ownedPools += $resource.pool
                $state.ownedSites += $resource.site
                Save-State
                $pool = $manager.ApplicationPools.Add($resource.pool)
                $pool.ManagedRuntimeVersion = 'v4.0'
                $pool.ManagedPipelineMode = 'Integrated'
                $pool.Enable32BitAppOnWin64 = $resource.bits -eq 32
                $pool.ProcessModel.IdentityType = 'ApplicationPoolIdentity'
                $site = $manager.Sites.Add($resource.site,'http',"127.0.0.1:$($resource.port):",$resource.webRoot)
                $site.Applications['/'].ApplicationPoolName = $resource.pool
                $manager.CommitChanges()
                if ($InterruptAt -eq 'AfterFirstSite') { [Environment]::Exit(197) }
                Grant-PoolRead $resource.webRoot $resource.pool
                # Use the pool identity for anonymous requests; no broad IUSR directory grant.
                $iisConfiguration = $manager.GetApplicationHostConfiguration()
                $anonymous = $iisConfiguration.GetSection('system.webServer/security/authentication/anonymousAuthentication',$resource.site)
                $anonymous.SetAttributeValue('userName','')
                $manager.CommitChanges()
                $state.ownedServices += $resource.service
                Save-State
                $null = New-Service -Name $resource.service -BinaryPathName $resource.binaryPath -StartupType Manual `
                    -DisplayName "FxDbg stage 3-4 environment ($($resource.architecture))"
                & sc.exe config $resource.service obj= 'NT AUTHORITY\LocalService'
                if ($LASTEXITCODE -ne 0) { throw 'Cannot set LocalService identity.' }
                Start-Service -Name $resource.service
            }
        } finally { $manager.Dispose() }
        Start-Service W3SVC
        Test-Environment
    }
    @{ action=$Action; succeeded=$true; atUtc=[DateTimeOffset]::UtcNow.ToString('o') } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $evidence 'last-operation.json') -Encoding UTF8
}
catch {
    if ($state) { $state.error=$_.Exception.Message; Save-State }
    @{ action=$Action; succeeded=$false; error=$_.Exception.Message; atUtc=[DateTimeOffset]::UtcNow.ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'last-operation.json') -Encoding UTF8
    throw
}
finally { Stop-Transcript | Out-Null }
