[CmdletBinding()]
param(
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'aoa-winusb-common.ps1')
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $repositoryRoot '.artifacts\aoa-winusb-driver\amd64'
}
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$device = Get-ExactAoaDevice
$before = Get-AoaDeviceSnapshot -Device $device
$adbBefore = Get-AdbInterfaceSnapshot
Assert-IsAdministrator

$inf = Join-Path $PackageDirectory 'panelos-aoa-winusb.inf'
$catalog = Join-Path $PackageDirectory 'panelos-aoa-winusb.cat'
Assert-PanelOsInfContent -InfPath $inf
if (-not (Test-Path -LiteralPath $catalog -PathType Leaf)) { throw "Catalog not found: $catalog" }
$signature = Get-AuthenticodeSignature -LiteralPath $catalog
if ($signature.Status -ne 'Valid') { throw "Catalog signature is $($signature.Status), expected Valid. No driver was installed." }

Write-Host "Target: $($before.RedactedInstanceId)"
Write-Host "Previous binding: INF=$($before.DriverInf), provider=$($before.DriverProvider), service=$($before.Service), class=$($before.Class)"
& "$env:WINDIR\System32\pnputil.exe" /add-driver $inf /install
if ($LASTEXITCODE -ne 0) { throw "PnPUtil installation failed with exit code $LASTEXITCODE." }
& "$env:WINDIR\System32\pnputil.exe" /scan-devices | Out-Null
Start-Sleep -Seconds 2

$verification = & (Join-Path $PSScriptRoot 'verify-aoa-winusb-driver.ps1')
$adbAfter = Get-AdbInterfaceSnapshot
if ($null -eq $adbBefore -or $null -eq $adbAfter) { throw 'ADB MI_01 was not present both before and after the MI_00 install.' }
if ($adbBefore.DriverInf -ine $adbAfter.DriverInf -or $adbBefore.DriverProvider -ine $adbAfter.DriverProvider) {
    throw "ADB MI_01 binding changed unexpectedly from $($adbBefore.DriverInf)/$($adbBefore.DriverProvider) to $($adbAfter.DriverInf)/$($adbAfter.DriverProvider)."
}
Write-Output $verification
Write-Host "ADB MI_01 preserved: INF=$($adbAfter.DriverInf), provider=$($adbAfter.DriverProvider), service=$($adbAfter.Service)."
