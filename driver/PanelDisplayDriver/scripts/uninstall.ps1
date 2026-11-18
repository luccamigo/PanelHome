<#
.SYNOPSIS
    Removes the PanelDisplayDriver software device and driver packages.

.DESCRIPTION
    THIS SCRIPT MODIFIES SYSTEM STATE. It does nothing unless you pass -Confirm.

    Without -Confirm it inventories present and phantom device records, matching
    driver packages, and PanelOS monitor records, then prints the exact supported
    PnP operations that would be performed. Explain-only mode does not require
    elevation.

    The adapter is a DevGen SOFTWARE DEVICE. DevGen /remove takes the complete
    device instance ID as a positional argument; /instanceid is an /add-only
    option. This script therefore removes SWD\DEVGEN\PanelDisplayDriver directly.

    It does not change Secure Boot, BCD, test signing, certificate stores, the
    registry, or any unrelated display adapter such as Parsec.

.PARAMETER Confirm
    Actually perform the removal.

.EXAMPLE
    powershell -File scripts\uninstall.ps1
    powershell -File scripts\uninstall.ps1 -Confirm
#>
[CmdletBinding()]
param(
    [switch]$Confirm
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$hardwareId = 'Root\PanelDisplayDriver'
$instanceId = 'PanelDisplayDriver'
$fullInstanceId = "SWD\DEVGEN\$instanceId"
$displayClassGuid = '{4d36e968-e325-11ce-bfc1-08002be10318}'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Find-Devgen {
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

    return [pscustomobject]@{
        InstanceId       = $fullInstanceId
        State            = $stateName
        Present          = $present
        Phantom          = $phantom
        Started          = $started
        StatusFlags      = $statusFlags
        ProblemCode      = $problemCode
        ProblemName      = Get-ProblemName -ProblemCode $problemCode
        HardwareIds      = @(Get-DevicePropertyData -DeviceInstanceId $fullInstanceId -KeyName 'DEVPKEY_Device_HardwareIds')
        DriverInfPath    = Get-DevicePropertyData -DeviceInstanceId $fullInstanceId -KeyName 'DEVPKEY_Device_DriverInfPath'
        DriverVersion    = Get-DevicePropertyData -DeviceInstanceId $fullInstanceId -KeyName 'DEVPKEY_Device_DriverVersion'
        DriverProvider   = Get-DevicePropertyData -DeviceInstanceId $fullInstanceId -KeyName 'DEVPKEY_Device_DriverProvider'
        Service          = Get-DevicePropertyData -DeviceInstanceId $fullInstanceId -KeyName 'DEVPKEY_Device_Service'
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

function Get-PanelDriverPackages {
    $document = Invoke-PnpUtilXml -Arguments @('/enum-drivers')
    $packages = New-Object System.Collections.Generic.List[object]

    # XPath is safe when the driver store has no matching Driver elements;
    # direct property access throws under StrictMode for an empty XML result.
    foreach ($driver in @($document.SelectNodes('/PnpUtil/Driver'))) {
        if ($null -eq $driver) { continue }

        if ([string]$driver.OriginalName -ine 'paneldisplaydriver.inf' -or
            [string]$driver.ProviderName -ine 'PanelOS' -or
            [string]$driver.ClassGuid -ine $displayClassGuid) {
            continue
        }

        $packages.Add([pscustomobject]@{
            PublishedName = [string]$driver.DriverName
            OriginalName  = [string]$driver.OriginalName
            ProviderName  = [string]$driver.ProviderName
            DriverVersion = [string]$driver.DriverVersion
        })
    }

    return $packages.ToArray()
}

function Get-MatchingDeviceRecords {
    $document = Invoke-PnpUtilXml -Arguments @('/enum-devices', '/deviceid', $hardwareId)
    $records = New-Object System.Collections.Generic.List[object]

    # A no-match query can contain only localized text under <PnpUtil>.
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
            Write-Host "  $($package.PublishedName): $($package.DriverVersion)"
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

function Wait-PanelDeviceAbsent {
    param(
        [int]$TimeoutSeconds = 30,
        [int]$StableSamples = 4
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $stableCount = 0
    $lastState = Get-PanelDeviceState

    do {
        $lastState = Get-PanelDeviceState
        if ($lastState.State -eq 'Absent') {
            $stableCount++
            if ($stableCount -ge $StableSamples) { return $lastState }
        } else {
            $stableCount = 0
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw ("Timed out waiting for the device record to become Absent. Last state: {0}; present={1}; problem={2}/{3} (0x{2:X2})." -f
        $lastState.State,
        $lastState.Present,
        $lastState.ProblemCode,
        $lastState.ProblemName)
}

function Wait-PanelPackagesRemoved {
    param(
        [int]$TimeoutSeconds = 15,
        [int]$StableSamples = 3
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $stableCount = 0
    $lastPackages = @()

    do {
        $lastPackages = @(Get-PanelDriverPackages)
        if ($lastPackages.Count -eq 0) {
            $stableCount++
            if ($stableCount -ge $StableSamples) { return }
        } else {
            $stableCount = 0
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    $names = @($lastPackages | ForEach-Object { $_.PublishedName }) -join ', '
    throw "Timed out waiting for driver-store removal. Packages still present: $names"
}

Write-Host 'PanelDisplayDriver - uninstall' -ForegroundColor Cyan
Write-Host '==============================='
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

if ($inventoryErrors.Count -gt 0) {
    Write-Host 'Cannot proceed:' -ForegroundColor Red
    foreach ($inventoryError in $inventoryErrors) { Write-Host "  - $inventoryError" -ForegroundColor Red }
    exit 1
}

$devgen = Find-Devgen
$hasDeviceRecord = $device.State -ne 'Absent'
$hasPackages = $packages.Count -gt 0
$hasChanges = $hasDeviceRecord -or $hasPackages

if ($hasDeviceRecord -and -not $devgen) {
    Write-Host 'Cannot proceed:' -ForegroundColor Red
    Write-Host '  - devgen.exe was not found under the Windows Kits x64 Tools directory or on PATH.' -ForegroundColor Red
    exit 1
}

Write-Host 'Planned changes:' -ForegroundColor Yellow
if (-not $hasChanges) {
    Write-Host '  <none> (adapter is absent and no matching package is in the driver store)'
} else {
    $step = 1
    if ($hasDeviceRecord) {
        Write-Host "  $step. devgen /remove `"$fullInstanceId`" /subtree"
        Write-Host "     -> removes the $($device.State) DevGen software device and waits until its record is Absent"
        $step++
    }

    foreach ($package in $packages) {
        Write-Host "  $step. pnputil /delete-driver $($package.PublishedName) /uninstall"
        Write-Host '     -> removes only this PanelOS driver package from devices and the driver store'
        $step++
    }
}

Write-Host ''
Write-Host 'PanelOS monitor records are diagnostic only and are not removed separately.' -ForegroundColor Yellow
Write-Host 'The script will NOT touch Secure Boot, BCD, test signing, certificate stores, the registry, or Parsec.' -ForegroundColor Yellow
Write-Host ''

if (-not $Confirm) {
    if (-not (Test-Administrator)) {
        Write-Host 'Explain-only mode is running without elevation; applying changes will require Administrator.' -ForegroundColor Yellow
    }
    Write-Host 'Explain-only mode. Nothing was changed.' -ForegroundColor Green
    Write-Host 'Re-run with -Confirm to apply the changes listed above.'
    exit 0
}

if (-not $hasChanges) {
    Write-Host 'Already uninstalled; no system state was changed.' -ForegroundColor Green
    exit 0
}

if (-not (Test-Administrator)) {
    throw 'Not running elevated. Re-run this script from an Administrator prompt.'
}

if ($hasDeviceRecord) {
    Write-Host '==> Removing the DevGen software device' -ForegroundColor Cyan
    & $devgen /remove $fullInstanceId /subtree
    $removeExit = $LASTEXITCODE
    if ($removeExit -ne 0) {
        $afterFailure = Get-PanelDeviceState
        throw "devgen /remove failed with exit code $removeExit. Device state is $($afterFailure.State), problem $($afterFailure.ProblemCode)/$($afterFailure.ProblemName)."
    }

    $device = Wait-PanelDeviceAbsent -TimeoutSeconds 30 -StableSamples 4
}

foreach ($package in $packages) {
    Write-Host "==> Removing driver package $($package.PublishedName)" -ForegroundColor Cyan
    & pnputil /delete-driver $package.PublishedName /uninstall
    if ($LASTEXITCODE -ne 0) {
        throw "pnputil /delete-driver failed with exit code $LASTEXITCODE for $($package.PublishedName)."
    }
}

Wait-PanelPackagesRemoved -TimeoutSeconds 15 -StableSamples 3

$device = Get-PanelDeviceState
$packages = @(Get-PanelDriverPackages)
$matchingDevices = @(Get-MatchingDeviceRecords)
$monitors = @(Get-PanelMonitorRecords)

if ($device.State -ne 'Absent') {
    throw "Post-uninstall validation failed: $fullInstanceId is still registered as $($device.State)."
}

if ($packages.Count -ne 0) {
    $names = @($packages | ForEach-Object { $_.PublishedName }) -join ', '
    throw "Post-uninstall validation failed: packages remain in the driver store: $names"
}

Write-Host 'Uninstall verified.' -ForegroundColor Green
Write-Host "  Adapter: $($device.State) (present=$($device.Present), phantom=$($device.Phantom))"
Write-Host '  Driver packages: <none>'

if ($monitors.Count -gt 0) {
    Write-Host '  PanelOS monitor records remaining (not active unless present):'
    foreach ($monitor in $monitors) {
        Write-Host "    $($monitor.InstanceId): status=$($monitor.Status), problem=$($monitor.Problem), present=$($monitor.Present)"
    }
}
