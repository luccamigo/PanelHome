[CmdletBinding()]
param(
    [switch]$AllowMissingAdb
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'aoa-winusb-common.ps1')
$device = Get-ExactAoaDevice
$snapshot = Get-AoaDeviceSnapshot -Device $device
if ($snapshot.HardwareIds -notcontains $script:PanelOsAoaHardwareId) { throw "Target hardware IDs do not contain $script:PanelOsAoaHardwareId." }
if ($snapshot.Service -ine 'WINUSB') { throw "MI_00 service is '$($snapshot.Service)', expected WINUSB." }
if ($snapshot.DriverProvider -ine $script:PanelOsDriverProvider) { throw "MI_00 provider is '$($snapshot.DriverProvider)', expected PanelOS." }
$installedInf = Get-SystemInfPath -PublishedName $snapshot.DriverInf
Assert-PanelOsInfContent -InfPath $installedInf
$deviceParameters = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($device.InstanceId)\Device Parameters"
$registeredGuids = @((Get-ItemProperty -LiteralPath $deviceParameters -Name DeviceInterfaceGUIDs -ErrorAction Stop).DeviceInterfaceGUIDs)
if ($registeredGuids -notcontains $script:PanelOsAoaInterfaceGuid.ToString('B')) { throw 'MI_00 does not register the PanelOS DeviceInterfaceGUIDs value.' }
$paths = @(Get-PanelOsAoaInterfacePaths)
if ($paths.Count -ne 1) { throw "Expected one present PanelOS MI_00 device-interface path, found $($paths.Count)." }

$adb = Get-AdbInterfaceSnapshot
if ($null -eq $adb -and -not $AllowMissingAdb) { throw 'AOA MI_01/ADB is not present after binding MI_00.' }
if ($null -ne $adb -and $adb.DriverProvider -ieq $script:PanelOsDriverProvider) { throw 'MI_01 was incorrectly bound to the PanelOS package.' }

$result = [ordered]@{
    verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    instanceId = $snapshot.RedactedInstanceId
    hardwareIds = $snapshot.HardwareIds
    compatibleIds = $snapshot.CompatibleIds
    currentInf = $snapshot.DriverInf
    currentProvider = $snapshot.DriverProvider
    currentService = $snapshot.Service
    class = $snapshot.Class
    classGuid = $snapshot.ClassGuid
    panelOsInterfaceGuid = $script:PanelOsAoaInterfaceGuid.ToString('B').ToUpperInvariant()
    presentInterfacePathCount = $paths.Count
    adbInterfacePresent = $null -ne $adb
    adbInf = if ($null -ne $adb) { $adb.DriverInf } else { $null }
    adbProvider = if ($null -ne $adb) { $adb.DriverProvider } else { $null }
    adbService = if ($null -ne $adb) { $adb.Service } else { $null }
}
$result | ConvertTo-Json -Depth 4
