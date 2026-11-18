<#
.SYNOPSIS
    Installs PanelDisplayDriver and ensures that its software device is healthy.

.DESCRIPTION
    THIS SCRIPT MODIFIES SYSTEM STATE. It does nothing unless you pass -Confirm.

    Without -Confirm it inventories the driver packages and device nodes, then
    prints the exact operations that would be performed. Explain-only mode does
    not require elevation.

    DevGen creates a SOFTWARE DEVICE by default. This script deliberately keeps
    that model; it does not pass /bus ROOT. The resulting instance ID is
    SWD\DEVGEN\PanelDisplayDriver even though the INF hardware ID is
    Root\PanelDisplayDriver.

    A healthy device already using the incoming package is reused. Every other
    existing instance is removed and allowed to settle before the package is
    staged and a new software device is created.

    The script deliberately does NOT change Secure Boot, BCD, test signing, or
    any certificate store.

.PARAMETER PackagePath
    Folder containing PanelDisplayDriver.inf, .cat and .dll. Defaults to the
    deterministic dist\Debug package produced by scripts\package.ps1.

.PARAMETER Confirm
    Actually perform the installation. Without it, nothing is changed.

.EXAMPLE
    powershell -File scripts\install.ps1
    powershell -File scripts\install.ps1 -Confirm
#>
[CmdletBinding()]
param(
    [string]$PackagePath,
    [switch]$Confirm
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

if (-not $PackagePath) {
    $PackagePath = Join-Path $root 'dist\Debug'
}

if (-not [IO.Path]::IsPathRooted($PackagePath)) {
    $PackagePath = Join-Path (Get-Location).Path $PackagePath
}
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$infPath = Join-Path $PackagePath 'PanelDisplayDriver.inf'
$hardwareId = 'Root\PanelDisplayDriver'
$instanceId = 'PanelDisplayDriver'
$fullInstanceId = "SWD\DEVGEN\$instanceId"
$displayClassGuid = '{4d36e968-e325-11ce-bfc1-08002be10318}'
$expectedSecuritySds = 'D:P(A;;GA;;;SY)(A;;GA;;;BA)'
$requiredPackageFiles = @(
    'PanelDisplayDriver.inf',
    'PanelDisplayDriver.cat',
    'PanelDisplayDriver.dll'
)

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Find-Devgen {
    # Prefer the newest x64 WDK copy. DevGen is not normally placed on PATH, and
    # selecting x64 explicitly avoids accidentally finding an ARM64 binary.
    $toolsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\Tools"
    if (Test-Path $toolsRoot) {
        $candidate = Get-ChildItem -Path $toolsRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object { try { [version]$_.Name } catch { [version]'0.0' } } -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\devgen.exe' } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1

        if ($candidate) { return $candidate }
    }

    $onPath = Get-Command devgen.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    return $null
}

function Find-PanelDisplayCtl {
    $packageConfiguration = Split-Path -Leaf $PackagePath
    $allConfigurations = @('Debug', 'Release')
    $configurations = if ($packageConfiguration -in @('Debug', 'Release')) {
        @($packageConfiguration) + @($allConfigurations | Where-Object { $_ -ne $packageConfiguration })
    } else {
        $allConfigurations
    }

    $candidates = @($configurations | ForEach-Object {
        Join-Path $root "tools\PanelDisplayCtl\x64\$_\PanelDisplayCtl.exe"
    })

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    $onPath = Get-Command PanelDisplayCtl.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    return $null
}

function Get-TestSigningState {
    # This is read-only. bcdedit needs elevation to read the store on many
    # machines, so explain mode reports unknown rather than failing.
    try {
        $output = & bcdedit /enum '{current}' 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $output) { return 'unknown (could not read BCD)' }
    } catch {
        return 'unknown (could not read BCD)'
    }

    $line = $output | Where-Object { $_ -match '^\s*testsigning\s+\S' } | Select-Object -First 1
    if (-not $line) { return 'OFF' }

    if ($line -match '^\s*testsigning\s+(\S+)') {
        $value = $Matches[1]
        if ($value -match '^(Yes|Sim|True|1)$') { return 'ON' }
        if ($value -match '^(No|N.o|False|0)$') { return 'OFF' }
        return "unknown (bcdedit reported '$value')"
    }

    return 'unknown'
}

if (-not ('PanelDisplayCfgMgr32' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class PanelDisplayCfgMgr32
{
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern UInt32 CM_Locate_DevNodeW(
        out UInt32 devInst,
        string deviceId,
        UInt32 flags);

    [DllImport("cfgmgr32.dll")]
    public static extern UInt32 CM_Get_DevNode_Status(
        out UInt32 status,
        out UInt32 problem,
        UInt32 devInst,
        UInt32 flags);
}
'@
}

function Get-DevicePropertyData {
    param(
        [string]$DeviceInstanceId,
        [string]$KeyName
    )

    $property = Get-PnpDeviceProperty -InstanceId $DeviceInstanceId -KeyName $KeyName -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if ($null -eq $property) { return $null }
    return $property.Data
}

function Get-ProblemName {
    param([uint32]$ProblemCode)

    switch ($ProblemCode) {
        0  { return 'CM_PROB_NONE' }
        10 { return 'CM_PROB_FAILED_START' }
        31 { return 'CM_PROB_FAILED_ADD' }
        43 { return 'CM_PROB_FAILED_POST_START' }
        45 { return 'CM_PROB_PHANTOM' }
        52 { return 'CM_PROB_UNSIGNED_DRIVER' }
        default { return 'CM_PROB_UNKNOWN' }
    }
}

function Get-PanelDeviceState {
    # CM_Locate_DevNode with NORMAL distinguishes a present devnode from a
    # registry-only phantom. PHANTOM then distinguishes that phantom from a
    # completely absent instance. This is independent of localized UI strings.
    $crSuccess = [uint32]0
    $crNoSuchDevNode = [uint32]0x0D
    $cmLocateNormal = [uint32]0
    $cmLocatePhantom = [uint32]1
    $dnStarted = [uint32]0x00000008
    $dnHasProblem = [uint32]0x00000400

    $devInst = [uint32]0
    $locateResult = [PanelDisplayCfgMgr32]::CM_Locate_DevNodeW(
        [ref]$devInst,
        $fullInstanceId,
        $cmLocateNormal)

    $present = $false
    $phantom = $false
    $statusFlags = [uint32]0
    $problemCode = [uint32]0

    if ($locateResult -eq $crSuccess) {
        $present = $true
        $statusResult = [PanelDisplayCfgMgr32]::CM_Get_DevNode_Status(
            [ref]$statusFlags,
            [ref]$problemCode,
            $devInst,
            0)

        if ($statusResult -ne $crSuccess) {
            throw ('CM_Get_DevNode_Status failed for {0}: CONFIGRET 0x{1:X8}.' -f $fullInstanceId, $statusResult)
        }

        if (($statusFlags -band $dnHasProblem) -eq 0) {
            $problemCode = [uint32]0
        }
    } elseif ($locateResult -eq $crNoSuchDevNode) {
        $phantomDevInst = [uint32]0
        $phantomResult = [PanelDisplayCfgMgr32]::CM_Locate_DevNodeW(
            [ref]$phantomDevInst,
            $fullInstanceId,
            $cmLocatePhantom)

        if ($phantomResult -eq $crSuccess) {
            $phantom = $true
            $problemCode = [uint32]45
        } elseif ($phantomResult -ne $crNoSuchDevNode) {
            throw ('CM_Locate_DevNode(PHANTOM) failed for {0}: CONFIGRET 0x{1:X8}.' -f $fullInstanceId, $phantomResult)
        }
    } else {
        throw ('CM_Locate_DevNode(NORMAL) failed for {0}: CONFIGRET 0x{1:X8}.' -f $fullInstanceId, $locateResult)
    }

    $started = $present -and (($statusFlags -band $dnStarted) -ne 0)
    $hasProblem = $present -and (($statusFlags -band $dnHasProblem) -ne 0)

    if ($present -and $started -and -not $hasProblem) {
        $stateName = 'Healthy'
    } elseif ($present -and $hasProblem) {
        $stateName = 'Problem'
    } elseif ($present) {
        $stateName = 'PresentNotStarted'
    } elseif ($phantom) {
        $stateName = 'Phantom'
    } else {
        $stateName = 'Absent'
    }

    $hardwareIds = @(Get-DevicePropertyData -DeviceInstanceId $fullInstanceId -KeyName 'DEVPKEY_Device_HardwareIds')

    return [pscustomobject]@{
        InstanceId       = $fullInstanceId
        State            = $stateName
        Present          = $present
        Phantom          = $phantom
        Started          = $started
        StatusFlags      = $statusFlags
        ProblemCode      = $problemCode
        ProblemName      = Get-ProblemName -ProblemCode $problemCode
        HardwareIds      = $hardwareIds
        DriverInfPath    = Get-DevicePropertyData -DeviceInstanceId $fullInstanceId -KeyName 'DEVPKEY_Device_DriverInfPath'
        DriverVersion    = Get-DevicePropertyData -DeviceInstanceId $fullInstanceId -KeyName 'DEVPKEY_Device_DriverVersion'
        DriverProvider   = Get-DevicePropertyData -DeviceInstanceId $fullInstanceId -KeyName 'DEVPKEY_Device_DriverProvider'
        Service          = Get-DevicePropertyData -DeviceInstanceId $fullInstanceId -KeyName 'DEVPKEY_Device_Service'
        SecuritySds      = Get-DevicePropertyData -DeviceInstanceId $fullInstanceId -KeyName 'DEVPKEY_Device_SecuritySDS'
    }
}

function Invoke-PnpUtilXml {
    param([string[]]$Arguments)

    $output = @(& pnputil @Arguments /format xml 2>$null)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "pnputil $($Arguments -join ' ') failed with exit code $exitCode."
    }

    try {
        return [xml]($output -join [Environment]::NewLine)
    } catch {
        throw "Could not parse pnputil XML for '$($Arguments -join ' ')': $($_.Exception.Message)"
    }
}

function Compare-PublishedPackageToIncoming {
    param([string]$PublishedName)

    foreach ($fileName in $requiredPackageFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $PackagePath $fileName))) {
            return 'Unknown'
        }
    }

    try {
        $publishedInf = Join-Path "$env:windir\INF" $PublishedName
        if (-not (Test-Path -LiteralPath $publishedInf)) { return 'Unknown' }

        $incomingInfHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $infPath).Hash
        $publishedInfHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $publishedInf).Hash
        if ($incomingInfHash -ne $publishedInfHash) { return 'Different' }

        $repositoryRoot = Join-Path "$env:windir\System32\DriverStore" 'FileRepository'
        $repositoryCandidates = Get-ChildItem -LiteralPath $repositoryRoot -Directory -Filter 'paneldisplaydriver.inf_*' -ErrorAction Stop

        foreach ($candidate in $repositoryCandidates) {
            $storedInf = Join-Path $candidate.FullName 'PanelDisplayDriver.inf'
            if (-not (Test-Path -LiteralPath $storedInf)) { continue }
            if ((Get-FileHash -Algorithm SHA256 -LiteralPath $storedInf).Hash -ne $publishedInfHash) { continue }

            $allFilesMatch = $true
            foreach ($fileName in $requiredPackageFiles) {
                $incomingFile = Join-Path $PackagePath $fileName
                $storedFile = Join-Path $candidate.FullName $fileName
                if (-not (Test-Path -LiteralPath $storedFile)) {
                    $allFilesMatch = $false
                    break
                }

                if ((Get-FileHash -Algorithm SHA256 -LiteralPath $incomingFile).Hash -ne
                    (Get-FileHash -Algorithm SHA256 -LiteralPath $storedFile).Hash) {
                    $allFilesMatch = $false
                    break
                }
            }

            if ($allFilesMatch) { return 'Current' }
        }

        return 'Different'
    } catch {
        return 'Unknown'
    }
}

function Get-PanelDriverPackages {
    $document = Invoke-PnpUtilXml -Arguments @('/enum-drivers')
    $packages = New-Object System.Collections.Generic.List[object]

    # XPath returns an empty collection when pnputil emits no Driver elements;
    # direct property access throws under StrictMode in that valid empty state.
    foreach ($driver in @($document.SelectNodes('/PnpUtil/Driver'))) {
        if ($null -eq $driver) { continue }

        $originalName = [string]$driver.OriginalName
        $providerName = [string]$driver.ProviderName
        $classGuid = [string]$driver.ClassGuid
        if ($originalName -ine 'paneldisplaydriver.inf' -or
            $providerName -ine 'PanelOS' -or
            $classGuid -ine $displayClassGuid) {
            continue
        }

        $publishedName = [string]$driver.DriverName
        $packages.Add([pscustomobject]@{
            PublishedName     = $publishedName
            OriginalName      = $originalName
            ProviderName      = $providerName
            DriverVersion     = [string]$driver.DriverVersion
            IncomingPackage   = Compare-PublishedPackageToIncoming -PublishedName $publishedName
        })
    }

    return $packages.ToArray()
}

function Get-MatchingDeviceRecords {
    $document = Invoke-PnpUtilXml -Arguments @('/enum-devices', '/deviceid', $hardwareId)
    $records = New-Object System.Collections.Generic.List[object]

    # A no-match query can contain only localized text under <PnpUtil>. XPath
    # keeps the genuinely Absent state distinct from a parser failure.
    foreach ($device in @($document.SelectNodes('/PnpUtil/Device'))) {
        if ($null -eq $device) { continue }
        $records.Add([pscustomobject]@{
            InstanceId = [string]$device.InstanceId
            Status     = [string]$device.Status
            DriverName = [string]$device.DriverName
        })
    }

    return $records.ToArray()
}

function Get-PanelMonitorRecords {
    return @(Get-PnpDevice -Class Monitor -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -like 'DISPLAY\PNL0001\*' })
}

function Write-Inventory {
    param(
        [object]$Device,
        [object[]]$Packages,
        [object[]]$MatchingDevices,
        [object[]]$Monitors
    )

    Write-Host 'PnP inventory:' -ForegroundColor Cyan
    Write-Host "  Instance:       $($Device.InstanceId)"
    Write-Host "  State:          $($Device.State)"
    Write-Host "  Present:        $($Device.Present)"
    Write-Host "  Phantom:        $($Device.Phantom)"
    Write-Host "  Started:        $($Device.Started)"
    Write-Host ('  Problem:        {0} / {1} (0x{0:X2})' -f $Device.ProblemCode, $Device.ProblemName)
    Write-Host ('  Status flags:   0x{0:X8}' -f $Device.StatusFlags)
    Write-Host "  Driver INF:     $(if ($Device.DriverInfPath) { $Device.DriverInfPath } else { '<none>' })"
    Write-Host "  Driver version: $(if ($Device.DriverVersion) { $Device.DriverVersion } else { '<none>' })"
    Write-Host "  Driver provider:$(if ($Device.DriverProvider) { ' ' + $Device.DriverProvider } else { ' <none>' })"
    Write-Host "  Service:        $(if ($Device.Service) { $Device.Service } else { '<none>' })"
    Write-Host "  Hardware IDs:   $(if ($Device.HardwareIds.Count -gt 0) { $Device.HardwareIds -join ', ' } else { '<none>' })"

    Write-Host '  Matching nodes:'
    if ($MatchingDevices.Count -eq 0) {
        Write-Host '    <none>'
    } else {
        foreach ($record in $MatchingDevices) {
            Write-Host "    $($record.InstanceId) [$($record.Status)] driver=$($record.DriverName)"
        }
    }

    Write-Host 'Driver packages:' -ForegroundColor Cyan
    if ($Packages.Count -eq 0) {
        Write-Host '  <none>'
    } else {
        foreach ($package in $Packages) {
            Write-Host "  $($package.PublishedName): $($package.DriverVersion); incoming=$($package.IncomingPackage)"
        }
    }

    Write-Host 'PanelOS monitor records:' -ForegroundColor Cyan
    if ($Monitors.Count -eq 0) {
        Write-Host '  <none>'
    } else {
        foreach ($monitor in $Monitors) {
            Write-Host "  $($monitor.InstanceId): status=$($monitor.Status), problem=$($monitor.Problem), present=$($monitor.Present)"
        }
    }
}

function Wait-PanelDeviceState {
    param(
        [ValidateSet('Absent', 'Healthy')]
        [string]$DesiredState,
        [int]$TimeoutSeconds = 30,
        [int]$StableSamples = 4
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $stableCount = 0
    $lastState = Get-PanelDeviceState

    do {
        $lastState = Get-PanelDeviceState
        $matches = if ($DesiredState -eq 'Absent') {
            $lastState.State -eq 'Absent'
        } else {
            $lastState.State -eq 'Healthy'
        }

        if ($matches) {
            $stableCount++
            if ($stableCount -ge $StableSamples) { return $lastState }
        } else {
            $stableCount = 0
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw ("Timed out waiting for {0}. Last state: {1}; present={2}; problem={3}/{4} (0x{3:X2})." -f
        $DesiredState,
        $lastState.State,
        $lastState.Present,
        $lastState.ProblemCode,
        $lastState.ProblemName)
}

function Wait-PanelControlReady {
    param(
        [string]$ControlPath,
        [switch]$RequireDisconnected,
        [int]$TimeoutSeconds = 15,
        [int]$StableSamples = 4
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $stableCount = 0
    $lastExitCode = -1
    $lastOutput = @()

    do {
        $lastOutput = @(& $ControlPath status 2>&1 | ForEach-Object { [string]$_ })
        $lastExitCode = $LASTEXITCODE
        $driverReady = $lastExitCode -eq 0 -and $lastOutput -contains 'PanelDisplayDriver: Ready'
        $monitorReady = if ($RequireDisconnected) {
            $lastOutput -contains 'Virtual monitor: Disconnected'
        } else {
            @($lastOutput | Where-Object { $_ -match '^Virtual monitor: (Connected|Disconnected)$' }).Count -eq 1
        }

        if ($driverReady -and $monitorReady) {
            $stableCount++
            if ($stableCount -ge $StableSamples) { return $lastOutput }
        } else {
            $stableCount = 0
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    $lastText = if ($lastOutput.Count -gt 0) { $lastOutput -join ' | ' } else { '<no output>' }
    throw "Timed out waiting for PanelDisplayCtl status to report Ready. Exit=$lastExitCode; output=$lastText"
}

function Test-HealthyIncomingDevice {
    param(
        [object]$Device,
        [object[]]$Packages
    )

    if ($Device.State -ne 'Healthy') { return $false }
    if ($Device.HardwareIds -inotcontains $hardwareId) { return $false }
    if (-not $Device.DriverInfPath) { return $false }
    if ($Device.SecuritySds -cne $expectedSecuritySds) { return $false }

    $associatedPackage = $Packages |
        Where-Object { $_.PublishedName -ieq $Device.DriverInfPath } |
        Select-Object -First 1

    return $null -ne $associatedPackage -and $associatedPackage.IncomingPackage -eq 'Current'
}

function Assert-PostInstallState {
    param(
        [object]$Device,
        [object[]]$Packages,
        [object[]]$MatchingDevices
    )

    if ($Device.State -ne 'Healthy') {
        throw "Post-install validation failed: device state is $($Device.State), problem $($Device.ProblemCode)/$($Device.ProblemName)."
    }

    if ($Device.HardwareIds -inotcontains $hardwareId) {
        throw "Post-install validation failed: $fullInstanceId does not expose hardware ID $hardwareId."
    }

    if ($Device.SecuritySds -cne $expectedSecuritySds) {
        throw "Post-install validation failed: device security is '$($Device.SecuritySds)', expected '$expectedSecuritySds'."
    }

    if ($MatchingDevices.Count -ne 1 -or $MatchingDevices[0].InstanceId -ine $fullInstanceId) {
        $ids = @($MatchingDevices | ForEach-Object { $_.InstanceId }) -join ', '
        throw "Post-install validation failed: expected exactly one matching adapter ($fullInstanceId), found: $ids"
    }

    $associatedPackage = $Packages |
        Where-Object { $_.PublishedName -ieq $Device.DriverInfPath } |
        Select-Object -First 1

    if ($null -eq $associatedPackage -or $associatedPackage.IncomingPackage -ne 'Current') {
        throw "Post-install validation failed: associated package '$($Device.DriverInfPath)' is not the incoming package."
    }
}

$missingPackageFiles = @($requiredPackageFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $PackagePath $_)) })
$devgen = Find-Devgen
$panelCtl = Find-PanelDisplayCtl
$testSigning = Get-TestSigningState

Write-Host 'PanelDisplayDriver - install' -ForegroundColor Cyan
Write-Host '============================='
Write-Host ''
Write-Host "Package:      $PackagePath"
Write-Host "INF:          $infPath"
Write-Host "Hardware id:  $hardwareId"
Write-Host "Instance id:  $fullInstanceId"
Write-Host "DevGen:       $(if ($devgen) { $devgen } else { '<not found>' })"
Write-Host "Control tool: $(if ($panelCtl) { $panelCtl } else { '<not found>' })"
Write-Host "Test signing: $testSigning"
Write-Host ''

$inventoryErrors = New-Object System.Collections.Generic.List[string]
$device = $null
$packages = @()
$matchingDevices = @()
$monitors = @()

try { $device = Get-PanelDeviceState } catch { $inventoryErrors.Add($_.Exception.Message) }
try { $packages = @(Get-PanelDriverPackages) } catch { $inventoryErrors.Add($_.Exception.Message) }
try { $matchingDevices = @(Get-MatchingDeviceRecords) } catch { $inventoryErrors.Add($_.Exception.Message) }
try { $monitors = @(Get-PanelMonitorRecords) } catch { $inventoryErrors.Add($_.Exception.Message) }

if ($null -ne $device) {
    Write-Inventory -Device $device -Packages $packages -MatchingDevices $matchingDevices -Monitors $monitors
    Write-Host ''
}

$problems = New-Object System.Collections.Generic.List[string]
foreach ($inventoryError in $inventoryErrors) { $problems.Add($inventoryError) }

if ($missingPackageFiles.Count -gt 0) {
    $problems.Add("Driver package is incomplete. Missing: $($missingPackageFiles -join ', ').")
}

if (-not $devgen) {
    $problems.Add('devgen.exe was not found under the Windows Kits x64 Tools directory or on PATH.')
}

if (-not $panelCtl) {
    $problems.Add('PanelDisplayCtl.exe was not found. Run scripts\build.ps1 so installation can validate Adapter Ready.')
}

if ($null -ne $device -and $matchingDevices.Count -gt 1) {
    $problems.Add("More than one devnode matches hardware ID $hardwareId; refusing to create another adapter.")
}

if ($problems.Count -gt 0) {
    Write-Host 'Cannot proceed:' -ForegroundColor Red
    foreach ($problem in $problems) { Write-Host "  - $problem" -ForegroundColor Red }
    exit 1
}

$reuseHealthyDevice = Test-HealthyIncomingDevice -Device $device -Packages $packages
$needsRecreation = -not $reuseHealthyDevice
$needsRemoval = $needsRecreation -and $device.State -ne 'Absent'

Write-Host 'Planned changes:' -ForegroundColor Yellow
if ($reuseHealthyDevice) {
    Write-Host '  <none>'
    Write-Host "  $fullInstanceId is healthy and already uses the incoming package."
} else {
    $step = 1
    if ($needsRemoval) {
        Write-Host "  $step. devgen /remove `"$fullInstanceId`" /subtree"
        Write-Host "     -> removes the existing $($device.State) software device and waits for State=Absent"
        $step++
    }

    Write-Host "  $step. pnputil /add-driver `"$infPath`""
    Write-Host '     -> stages the package without installing/restarting matching devnodes'
    $step++
    Write-Host "  $step. devgen /add /hardwareid `"$hardwareId`" /instanceid `"$instanceId`""
    Write-Host '     -> creates the SWD, requires five seconds healthy, then validates Adapter Ready via PanelDisplayCtl'
}

Write-Host ''
Write-Host 'It will NOT touch Secure Boot, BCD, test signing, certificate stores, the registry, or Parsec.' -ForegroundColor Yellow
Write-Host ''

if (-not $Confirm) {
    if (-not (Test-Administrator)) {
        Write-Host 'Explain-only mode is running without elevation; applying changes will require Administrator.' -ForegroundColor Yellow
    }
    Write-Host 'Explain-only mode. Nothing was changed.' -ForegroundColor Green
    Write-Host 'Re-run with -Confirm to apply the changes listed above.'
    exit 0
}

if (-not (Test-Administrator)) {
    throw 'Not running elevated. Re-run this script from an Administrator prompt.'
}

if ($reuseHealthyDevice) {
    # The previous post-start failure appeared about two seconds after the node
    # first reported DN_STARTED. Require five seconds of continuously healthy
    # polling so a transient start cannot be reported as a successful install.
    $device = Wait-PanelDeviceState -DesiredState Healthy -TimeoutSeconds 10 -StableSamples 21
    $packages = @(Get-PanelDriverPackages)
    $matchingDevices = @(Get-MatchingDeviceRecords)
    Assert-PostInstallState -Device $device -Packages $packages -MatchingDevices $matchingDevices
    $controlStatus = @(Wait-PanelControlReady -ControlPath $panelCtl -TimeoutSeconds 15 -StableSamples 4)
    Write-Host 'Already installed and healthy; no system state was changed.' -ForegroundColor Green
} else {
    if ($needsRemoval) {
        Write-Host '==> Removing the existing software device' -ForegroundColor Cyan
        & $devgen /remove $fullInstanceId /subtree
        $removeExit = $LASTEXITCODE
        if ($removeExit -ne 0) {
            $afterFailure = Get-PanelDeviceState
            throw "devgen /remove failed with exit code $removeExit. Device state is $($afterFailure.State), problem $($afterFailure.ProblemCode)/$($afterFailure.ProblemName)."
        }

        $null = Wait-PanelDeviceState -DesiredState Absent -TimeoutSeconds 30 -StableSamples 4
    }

    Write-Host '==> Staging the driver package (no /install)' -ForegroundColor Cyan
    & pnputil /add-driver $infPath
    if ($LASTEXITCODE -ne 0) {
        throw "pnputil /add-driver failed with exit code $LASTEXITCODE."
    }

    $packages = @(Get-PanelDriverPackages)
    if (-not ($packages | Where-Object { $_.IncomingPackage -eq 'Current' })) {
        throw 'pnputil returned success, but the incoming package could not be verified in the driver store.'
    }

    Write-Host '==> Creating the software device' -ForegroundColor Cyan
    & $devgen /add /hardwareid $hardwareId /instanceid $instanceId
    $addExit = $LASTEXITCODE
    if ($addExit -ne 0) {
        $afterFailure = Get-PanelDeviceState
        throw "devgen /add failed with exit code $addExit. Device state is $($afterFailure.State), problem $($afterFailure.ProblemCode)/$($afterFailure.ProblemName)."
    }

    $device = Wait-PanelDeviceState -DesiredState Healthy -TimeoutSeconds 30 -StableSamples 21
    $packages = @(Get-PanelDriverPackages)
    $matchingDevices = @(Get-MatchingDeviceRecords)
    Assert-PostInstallState -Device $device -Packages $packages -MatchingDevices $matchingDevices
    $controlStatus = @(Wait-PanelControlReady -ControlPath $panelCtl -RequireDisconnected -TimeoutSeconds 15 -StableSamples 4)

    Write-Host 'Installed; PnP start and Adapter Ready verified.' -ForegroundColor Green
}

Write-Host "  Device:   $fullInstanceId ($($device.State))"
Write-Host "  Driver:   $($device.DriverInfPath) $($device.DriverVersion)"
Write-Host "  Security: $(if ($device.SecuritySds) { $device.SecuritySds } else { '<none>' })"
Write-Host "  Control:  $($controlStatus -join ' / ')"
Write-Host ''
Write-Host 'Next validation: PanelDisplayCtl status'
Write-Host 'Expected:        PanelDisplayDriver: Ready / Virtual monitor: Disconnected'
