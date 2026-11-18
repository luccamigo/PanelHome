$script:PanelOsAoaHardwareId = 'USB\VID_18D1&PID_2D01&MI_00'
$script:PanelOsAdbHardwareId = 'USB\VID_18D1&PID_2D01&MI_01'
$script:PanelOsAoaInterfaceGuid = [Guid]'4F9B4A5C-7A77-4B21-9E4A-5C4C54D98E9D'
$script:PanelOsDriverProvider = 'PanelOS'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-IsAdministrator {
    if (-not (Test-IsAdministrator)) {
        throw 'This operation requires an elevated PowerShell (Run as administrator). No system state was changed.'
    }
}

function Get-PnpPropertyValue {
    param(
        [Parameter(Mandatory = $true)][string]$InstanceId,
        [Parameter(Mandatory = $true)][string]$KeyName
    )
    $property = Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName $KeyName -ErrorAction SilentlyContinue
    if ($null -eq $property) { return $null }
    return $property.Data
}

function Get-ExactAoaDevice {
    $matches = @(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object {
        if ($_.InstanceId -notmatch '(?i)USB\\VID_18D1&PID_2D01') { return $false }
        $hardwareIds = @(Get-PnpPropertyValue -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds')
        return $hardwareIds -contains $script:PanelOsAoaHardwareId
    })
    if ($matches.Count -eq 0) {
        throw 'PanelOS AOA interface not currently present. Put the tablet into accessory mode first.'
    }
    if ($matches.Count -ne 1) {
        throw "Expected exactly one $script:PanelOsAoaHardwareId interface, found $($matches.Count)."
    }
    return $matches[0]
}

function Get-AoaDeviceSnapshot {
    param([Parameter(Mandatory = $true)]$Device)

    $hardwareIds = @(Get-PnpPropertyValue -InstanceId $Device.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds')
    $compatibleIds = @(Get-PnpPropertyValue -InstanceId $Device.InstanceId -KeyName 'DEVPKEY_Device_CompatibleIds')
    $driverInf = [string](Get-PnpPropertyValue -InstanceId $Device.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath')
    $provider = [string](Get-PnpPropertyValue -InstanceId $Device.InstanceId -KeyName 'DEVPKEY_Device_DriverProvider')
    $service = [string](Get-PnpPropertyValue -InstanceId $Device.InstanceId -KeyName 'DEVPKEY_Device_Service')
    $classGuid = [string](Get-PnpPropertyValue -InstanceId $Device.InstanceId -KeyName 'DEVPKEY_Device_ClassGuid')
    [pscustomobject]@{
        InstanceId = $Device.InstanceId
        RedactedInstanceId = "$script:PanelOsAoaHardwareId\<redacted>"
        HardwareIds = $hardwareIds
        CompatibleIds = $compatibleIds
        DriverInf = $driverInf
        DriverProvider = $provider
        Service = $service
        Class = $Device.Class
        ClassGuid = $classGuid
        Status = $Device.Status
    }
}

function Get-AdbInterfaceSnapshot {
    $device = @(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object {
        if ($_.InstanceId -notmatch '(?i)USB\\VID_18D1&PID_2D01') { return $false }
        $hardwareIds = @(Get-PnpPropertyValue -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds')
        return $hardwareIds -contains $script:PanelOsAdbHardwareId
    }) | Select-Object -First 1
    if ($null -eq $device) { return $null }
    return [pscustomobject]@{
        InstanceId = $device.InstanceId
        RedactedInstanceId = "$script:PanelOsAdbHardwareId\<redacted>"
        DriverInf = [string](Get-PnpPropertyValue -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath')
        DriverProvider = [string](Get-PnpPropertyValue -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_DriverProvider')
        Service = [string](Get-PnpPropertyValue -InstanceId $device.InstanceId -KeyName 'DEVPKEY_Device_Service')
        Status = $device.Status
    }
}

function Initialize-DeviceInterfaceProbe {
    if ('PanelOs.DeviceInterfaceProbe' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PanelOs
{
    public static class DeviceInterfaceProbe
    {
        private const uint Present = 0x2;
        private const uint DeviceInterface = 0x10;
        private const int NoMoreItems = 259;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct InterfaceData
        {
            public int Size;
            public Guid InterfaceClassGuid;
            public int Flags;
            public UIntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr info, IntPtr data, ref Guid classGuid, uint index, ref InterfaceData interfaceData);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr info, ref InterfaceData interfaceData, IntPtr detail, uint detailSize, out uint required, IntPtr deviceInfo);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr info);

        public static string[] Enumerate(Guid classGuid)
        {
            var result = new List<string>();
            var info = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, Present | DeviceInterface);
            if (info == InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for (uint index = 0; ; index++)
                {
                    var data = new InterfaceData { Size = Marshal.SizeOf(typeof(InterfaceData)) };
                    if (!SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref classGuid, index, ref data))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == NoMoreItems) break;
                        throw new Win32Exception(error);
                    }
                    uint required;
                    SetupDiGetDeviceInterfaceDetail(info, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    var detail = Marshal.AllocHGlobal(checked((int)required));
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(info, ref data, detail, required, out required, IntPtr.Zero))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (!String.IsNullOrWhiteSpace(path)) result.Add(path);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(info); }
            return result.ToArray();
        }
    }
}
'@
}

function Get-PanelOsAoaInterfacePaths {
    Initialize-DeviceInterfaceProbe
    return @([PanelOs.DeviceInterfaceProbe]::Enumerate($script:PanelOsAoaInterfaceGuid))
}

function Assert-PanelOsInfContent {
    param([Parameter(Mandatory = $true)][string]$InfPath)
    if (-not (Test-Path -LiteralPath $InfPath -PathType Leaf)) { throw "INF not found: $InfPath" }
    $content = Get-Content -LiteralPath $InfPath -Raw
    if ($content -notmatch '(?im)^Class\s*=\s*USBDevice\s*$') { throw 'INF must use Class=USBDevice.' }
    if ($content -notmatch '(?im)^ClassGuid\s*=\s*\{88BAE032-5A81-49F0-BC3D-A4FF138216D6\}\s*$') { throw 'INF has the wrong USBDevice class GUID.' }
    if ($content -notmatch '(?im)^\s*%AccessoryDevice%\s*=\s*WinUSB_Install\s*,\s*USB\\VID_18D1&PID_2D01&MI_00\s*$') { throw "INF does not contain the exact $script:PanelOsAoaHardwareId model." }
    if ($content -match '(?im),\s*USB\\VID_18D1&PID_2D01\s*$' -or $content -match '(?i)PID_2D01&MI_01' -or $content -match '(?i)PID_2D00') { throw 'INF contains a forbidden whole-device, ADB, or alternate-PID match.' }
    if ($content -notmatch '(?i)DeviceInterfaceGUIDs' -or $content -notmatch [regex]::Escape($script:PanelOsAoaInterfaceGuid.ToString())) { throw 'INF does not register the PanelOS device-interface GUID.' }
}

function Get-SystemInfPath {
    param([Parameter(Mandatory = $true)][string]$PublishedName)
    if ($PublishedName -notmatch '^oem\d+\.inf$') { throw "Refusing unexpected published INF name: $PublishedName" }
    return Join-Path $env:WINDIR "INF\$PublishedName"
}
