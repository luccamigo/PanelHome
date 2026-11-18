[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$CertificateThumbprint,
    [ValidateSet('CurrentUser', 'LocalMachine')][string]$CertificateLocation = 'CurrentUser',
    [string]$TimestampUrl,
    [string]$OperatingSystems = '10_X64,10_GE_X64'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'aoa-winusb-common.ps1')
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot '.artifacts\aoa-winusb-driver\amd64'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$sourceInf = Join-Path $repositoryRoot 'windows\PanelHost\Transport\Driver\panelos-aoa-winusb.inf'
Assert-PanelOsInfContent -InfPath $sourceInf

$kitsBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$inf2Cat = Get-ChildItem -LiteralPath $kitsBin -Filter Inf2Cat.exe -Recurse -File -ErrorAction SilentlyContinue |
    Sort-Object { try { [version]$_.Directory.Parent.Name } catch { [version]'0.0' } } -Descending |
    Select-Object -First 1
$signTool = Get-ChildItem -LiteralPath $kitsBin -Filter SignTool.exe -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -eq 'x64' } |
    Sort-Object { try { [version]$_.Directory.Parent.Name } catch { [version]'0.0' } } -Descending |
    Select-Object -First 1
if ($null -eq $inf2Cat) { throw 'WDK Inf2Cat.exe was not found. Install the Windows Driver Kit.' }
if ($null -eq $signTool) { throw 'Windows SDK SignTool.exe (x64) was not found.' }

if (Test-Path -LiteralPath $OutputDirectory) {
    $resolvedOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.artifacts'))
    if (-not $resolvedOutput.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -and -not $PSBoundParameters.ContainsKey('OutputDirectory')) {
        throw "Refusing to replace an output directory outside $allowedRoot."
    }
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$packageInf = Join-Path $OutputDirectory 'panelos-aoa-winusb.inf'
Copy-Item -LiteralPath $sourceInf -Destination $packageInf

Write-Host "Inf2Cat: $($inf2Cat.FullName)"
& $inf2Cat.FullName "/driver:$OutputDirectory" "/os:$OperatingSystems" /verbose
if ($LASTEXITCODE -ne 0) { throw "Inf2Cat failed with exit code $LASTEXITCODE." }
$catalog = Join-Path $OutputDirectory 'panelos-aoa-winusb.cat'
if (-not (Test-Path -LiteralPath $catalog)) { throw "Inf2Cat did not produce $catalog." }

$signingStatus = 'Unsigned'
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $normalizedThumbprint = $CertificateThumbprint -replace '\s', ''
    $storePath = "Cert:\$CertificateLocation\My\$normalizedThumbprint"
    $certificate = Get-Item -LiteralPath $storePath -ErrorAction SilentlyContinue
    if ($null -eq $certificate) { throw "Signing certificate was not found at $storePath." }
    if (-not $certificate.HasPrivateKey) { throw 'The selected signing certificate has no accessible private key.' }
    if ($certificate.NotAfter -le (Get-Date)) { throw 'The selected signing certificate is expired.' }
    $arguments = @('sign', '/v', '/fd', 'SHA256', '/s', 'My', '/sha1', $normalizedThumbprint)
    if ($CertificateLocation -eq 'LocalMachine') { $arguments += '/sm' }
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) { $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    $arguments += $catalog
    & $signTool.FullName @arguments
    if ($LASTEXITCODE -ne 0) { throw "SignTool sign failed with exit code $LASTEXITCODE." }
    & $signTool.FullName verify /v /pa $catalog
    if ($LASTEXITCODE -ne 0) { throw "SignTool verification failed with exit code $LASTEXITCODE." }
    $signingStatus = (Get-AuthenticodeSignature -LiteralPath $catalog).Status.ToString()
    if ($signingStatus -ne 'Valid') { throw "Catalog signature status is $signingStatus, expected Valid." }
}

$manifest = [ordered]@{
    schemaVersion = 1
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    architecture = 'amd64'
    hardwareId = $script:PanelOsAoaHardwareId
    deviceInterfaceGuid = $script:PanelOsAoaInterfaceGuid.ToString('B').ToUpperInvariant()
    operatingSystems = $OperatingSystems
    infSha256 = (Get-FileHash -LiteralPath $packageInf -Algorithm SHA256).Hash
    catalogSha256 = (Get-FileHash -LiteralPath $catalog -Algorithm SHA256).Hash
    catalogSignature = $signingStatus
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'package-manifest.json') -Encoding utf8
Write-Host "AOA WinUSB package: $OutputDirectory"
Write-Host "Catalog signature: $signingStatus"
if ($signingStatus -eq 'Unsigned') {
    Write-Warning 'The package is not installable on Windows x64. Re-run with -CertificateThumbprint explicitly.'
}
