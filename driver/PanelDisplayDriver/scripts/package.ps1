<#
.SYNOPSIS
    Collects the built driver package into dist/ for manual installation.

.DESCRIPTION
    The WDK build already produces a driver package folder next to the driver
    binary, containing the INF, the CAT and the DLL. This script verifies that
    package is complete and copies it to dist/<Configuration>/ so there is one
    obvious folder to hand to install.ps1 or copy to a test machine.

    It validates that CAT, DLL and CER use the same signer, but it never adds a
    signature and never installs a certificate. Debug builds are normally
    test-signed automatically by the WDK; production signing remains outside
    this script. See README.md, "Signing for development".

    This script only reads the build output and writes to dist/. It changes no
    system state.

.PARAMETER Configuration
    Debug (default) or Release.

.EXAMPLE
    pwsh -File scripts/package.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$platform = 'x64'

$sourcePackage = Join-Path $root "driver\$platform\$Configuration\PanelDisplayDriver"
$distRoot = Join-Path $root 'dist'
$destination = Join-Path $distRoot $Configuration

Write-Host "PanelDisplayDriver - package" -ForegroundColor Cyan
Write-Host "============================"
Write-Host ""
Write-Host "Source: $sourcePackage"
Write-Host "Output: $destination"
Write-Host ""

if (-not (Test-Path $sourcePackage)) {
    Write-Host "Driver package not found." -ForegroundColor Red
    Write-Host "Build the driver first: pwsh -File scripts/build.ps1 -Configuration $Configuration"
    Write-Host "(That requires the WDK - see README.md, 'Requirements'.)"
    exit 1
}

$required = @(
    'PanelDisplayDriver.inf',
    'PanelDisplayDriver.cat',
    'PanelDisplayDriver.dll'
)

$requiredExtras = @(
    'PanelDisplayDriver.cer',
    'PanelDisplayDriver.pdb'
)

# @() so a single missing file still yields a countable array under StrictMode.
$missing = @($required | Where-Object { -not (Test-Path (Join-Path $sourcePackage $_)) })

if ($missing.Count -gt 0) {
    Write-Host "The driver package is incomplete. Missing:" -ForegroundColor Red
    foreach ($file in $missing) { Write-Host "  - $file" -ForegroundColor Red }
    exit 1
}

$missingExtras = @($requiredExtras | Where-Object {
    -not (Test-Path (Join-Path $root "driver\$platform\$Configuration\$_"))
})

if ($missingExtras.Count -gt 0) {
    Write-Host "The driver package is incomplete. Missing required symbols/signing material:" -ForegroundColor Red
    foreach ($file in $missingExtras) { Write-Host "  - $file" -ForegroundColor Red }
    exit 1
}

# Publish through a clean staging directory so stale files from an older build
# can never survive in dist. Resolve and validate both paths before the one
# recursive removal this entails.
$distFull = [IO.Path]::GetFullPath($distRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$destinationFull = [IO.Path]::GetFullPath($destination)
$expectedPrefix = $distFull + [IO.Path]::DirectorySeparatorChar

if (-not $destinationFull.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace package outside '$distFull': '$destinationFull'."
}

if (-not (Test-Path $distRoot)) {
    New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
} else {
    $distItem = Get-Item -LiteralPath $distRoot -Force
    if (-not $distItem.PSIsContainer -or
        ($distItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to publish through a non-directory or reparse point: '$distFull'."
    }
}

$staging = Join-Path $distRoot (".{0}.staging-{1}" -f $Configuration, [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null

try {
    foreach ($file in $required) {
        Copy-Item -LiteralPath (Join-Path $sourcePackage $file) -Destination $staging
    }

    # The PDB and generated test certificate live beside the binary rather than
    # in the WDK package folder, but both are required for this development
    # package: the PDB makes dumps diagnosable and the CER identifies the test
    # signer. Packaging does not install or trust the certificate.
    foreach ($extra in $requiredExtras) {
        Copy-Item -LiteralPath (Join-Path $root "driver\$platform\$Configuration\$extra") -Destination $staging
    }

    # Validate the staged bytes before replacing a previously good dist folder.
    # A failed or mismatched signature leaves the existing published package
    # untouched and the finally block removes only this temporary staging tree.
    $stagingCatalog = Join-Path $staging 'PanelDisplayDriver.cat'
    $stagingDriverBinary = Join-Path $staging 'PanelDisplayDriver.dll'
    $stagingCertificatePath = Join-Path $staging 'PanelDisplayDriver.cer'
    $signature = Get-AuthenticodeSignature -FilePath $stagingCatalog
    $driverSignature = Get-AuthenticodeSignature -FilePath $stagingDriverBinary
    $packagedCertificate = New-Object -TypeName System.Security.Cryptography.X509Certificates.X509Certificate2 `
        -ArgumentList $stagingCertificatePath

    if (-not $signature.SignerCertificate) {
        throw 'Staged catalog has no signer. Refusing to publish an unsigned driver package.'
    }

    if (-not $driverSignature.SignerCertificate) {
        throw 'Staged driver DLL has no signer. Refusing to publish an unsigned driver package.'
    }

    $catalogThumbprint = $signature.SignerCertificate.Thumbprint
    $driverThumbprint = $driverSignature.SignerCertificate.Thumbprint
    $certificateThumbprint = $packagedCertificate.Thumbprint

    if ($catalogThumbprint -ine $driverThumbprint -or $catalogThumbprint -ine $certificateThumbprint) {
        throw ("Package signer mismatch. CAT={0}; DLL={1}; CER={2}." -f
            $catalogThumbprint, $driverThumbprint, $certificateThumbprint)
    }

    if (Test-Path $destinationFull) {
        $destinationItem = Get-Item -LiteralPath $destinationFull -Force
        if (-not $destinationItem.PSIsContainer -or
            ($destinationItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to recursively replace a non-directory or reparse point: '$destinationFull'."
        }
        Remove-Item -LiteralPath $destinationFull -Recurse -Force
    }

    Move-Item -LiteralPath $staging -Destination $destinationFull
} finally {
    if (Test-Path $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

Write-Host "Packaged files:" -ForegroundColor Green
Get-ChildItem $destination | ForEach-Object { Write-Host ("  {0,-32} {1,10:N0} bytes" -f $_.Name, $_.Length) }

Write-Host ""

Write-Host "Catalog signature status: $($signature.Status)"
Write-Host "DLL signature status:     $($driverSignature.Status)"
Write-Host "Package signed by:        $($signature.SignerCertificate.Subject)"
Write-Host "CAT/DLL/CER signer match: $catalogThumbprint"

if ($signature.SignerCertificate.Subject -match 'WDKTestCert') {
    Write-Host "Signature purpose:        WDK test signing (not production signing)" -ForegroundColor Yellow
}

if ($signature.Status -eq 'Valid' -and $driverSignature.Status -eq 'Valid') {
    # Nothing to warn about.
} else {
    # This is the normal outcome of a WDK Debug build: the package is signed with
    # a self-generated WDKTestCert that no machine trusts by default. Saying
    # "unsigned" here would send the reader down the wrong path.
    Write-Host ""
    Write-Host "The CAT and DLL ARE signed, but this machine does not report both as Valid" -ForegroundColor Yellow
    Write-Host "(expected for a WDK test certificate). Before the driver will load you" -ForegroundColor Yellow
    Write-Host "must, as deliberate manual steps:" -ForegroundColor Yellow
    Write-Host "  1. trust PanelDisplayDriver.cer on the target machine, and" -ForegroundColor Yellow
    Write-Host "  2. put that machine in test signing mode." -ForegroundColor Yellow
    Write-Host "Neither is done by any script here. See README.md, 'Signing for development'." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Next: pwsh -File scripts/install.ps1 -PackagePath `"$destination`"" -ForegroundColor Cyan
