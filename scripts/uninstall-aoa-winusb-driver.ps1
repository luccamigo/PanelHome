[CmdletBinding()]
param(
    [string]$PublishedName
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'aoa-winusb-common.ps1')
Assert-IsAdministrator
$adbBefore = Get-AdbInterfaceSnapshot
if ([string]::IsNullOrWhiteSpace($PublishedName)) {
    $device = Get-ExactAoaDevice
    $snapshot = Get-AoaDeviceSnapshot -Device $device
    if ($snapshot.DriverProvider -ine $script:PanelOsDriverProvider) { throw 'MI_00 is not currently bound to a PanelOS driver package.' }
    $PublishedName = $snapshot.DriverInf
}
$installedInf = Get-SystemInfPath -PublishedName $PublishedName
Assert-PanelOsInfContent -InfPath $installedInf
$content = Get-Content -LiteralPath $installedInf -Raw
if ($content -notmatch '(?im)^Provider\s*=\s*%ProviderName%\s*$' -or $content -notmatch '(?im)^ProviderName\s*=\s*"PanelOS"\s*$') {
    throw 'Refusing to remove a package whose INF provider is not PanelOS.'
}

& "$env:WINDIR\System32\pnputil.exe" /delete-driver $PublishedName /uninstall
if ($LASTEXITCODE -ne 0) { throw "PnPUtil uninstall failed with exit code $LASTEXITCODE." }
Start-Sleep -Seconds 2
if (Test-Path -LiteralPath $installedInf) { throw "$PublishedName is still present in the Windows INF store." }
$adbAfter = Get-AdbInterfaceSnapshot
if ($null -ne $adbBefore -and ($null -eq $adbAfter -or $adbBefore.DriverInf -ine $adbAfter.DriverInf)) {
    throw 'ADB MI_01 did not retain its previous driver binding during rollback.'
}
Write-Host "Removed only the PanelOS package $PublishedName. Windows may rebind MI_00 to its previous in-box/OEM driver."
