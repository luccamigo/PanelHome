<#
.SYNOPSIS
    Builds PanelDisplayDriver and runs the unit tests.

.DESCRIPTION
    Builds three projects:

      PanelDisplayDriver.Tests  always (needs only the Windows SDK)
      PanelDisplayCtl           always (needs only the Windows SDK)
      PanelDisplayDriver        only when the WDK is installed

    The driver requires the WindowsUserModeDriver10.0 platform toolset, which
    ships with the Windows Driver Kit. If the WDK is missing this script says so
    and still builds and tests everything else, rather than failing outright.

    This script only compiles and runs tests. It changes no system state.

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER SkipTests
    Build without running the unit tests.

.PARAMETER DriverOnly
    Build only the driver project. Fails if the WDK is not installed.

.EXAMPLE
    pwsh -File scripts/build.ps1
    pwsh -File scripts/build.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$SkipTests,
    [switch]$DriverOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$platform = 'x64'

function Test-WdkToolset {
    param([string]$InstallPath)

    # The WDK installs its platform toolset into a specific Visual Studio
    # installation. Its presence there is what actually decides whether the
    # driver project can load, so this is the only reliable probe.
    $vcRoot = Join-Path $InstallPath 'MSBuild\Microsoft\VC'
    if (-not (Test-Path $vcRoot)) { return $false }

    $found = Get-ChildItem -Path $vcRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        Join-Path $_.FullName "Platforms\$platform\PlatformToolsets\WindowsUserModeDriver10.0"
    } | Where-Object { Test-Path $_ }

    return [bool]$found
}

function Get-MSBuildPath {
    param([string]$InstallPath)

    # The 64-bit MSBuild is required, not merely preferred: the WDK's INF
    # verification task loads a native infverif DLL matching the MSBuild
    # process architecture, and recent WDKs ship only x64/arm64 builds of it.
    # Under 32-bit MSBuild the task fails with "cannot load x86\InfVerif.dll"
    # and the package is emitted unverified.
    $candidate = Join-Path $InstallPath 'MSBuild\Current\Bin\amd64\MSBuild.exe'
    if (Test-Path $candidate) { return $candidate }

    return $null
}

function Find-Toolchain {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        throw "vswhere.exe not found. Install Visual Studio 2022 (or newer) with the Desktop development with C++ workload."
    }

    # -products * so Build Tools installations are found, not just the full IDE.
    $installPaths = @(& $vswhere -all -products * -requires Microsoft.Component.MSBuild -property installationPath)
    if ($installPaths.Count -eq 0) {
        throw "No Visual Studio installation with MSBuild was found."
    }

    # More than one VS installation is common, and the WDK only integrates into
    # one of them. Pick the one that can actually build the driver rather than
    # whichever happens to be newest.
    foreach ($installPath in $installPaths) {
        if (Test-WdkToolset -InstallPath $installPath) {
            $msbuild = Get-MSBuildPath -InstallPath $installPath
            if ($msbuild) {
                return [pscustomobject]@{ MSBuild = $msbuild; InstallPath = $installPath; Wdk = $true }
            }
        }
    }

    foreach ($installPath in $installPaths) {
        $msbuild = Get-MSBuildPath -InstallPath $installPath
        if ($msbuild) {
            return [pscustomobject]@{ MSBuild = $msbuild; InstallPath = $installPath; Wdk = $false }
        }
    }

    throw "64-bit MSBuild.exe was not found under MSBuild\Current\Bin\amd64 in any Visual Studio installation. The 32-bit MSBuild is deliberately unsupported because WDK InfVerif 10.0.28000.0 requires an x64 host."
}

function Invoke-MSBuild {
    param([string]$MSBuildPath, [string]$ProjectPath, [string]$Label)

    Write-Host "==> Building $Label ($Configuration|$platform)" -ForegroundColor Cyan

    & $MSBuildPath $ProjectPath "/p:Configuration=$Configuration" "/p:Platform=$platform" /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed for $Label."
    }
}

$toolchain = Find-Toolchain
$msbuild = $toolchain.MSBuild
$wdkInstalled = $toolchain.Wdk

Write-Host "Visual Studio: $($toolchain.InstallPath)"
Write-Host "MSBuild: $msbuild"
Write-Host "WDK (WindowsUserModeDriver10.0 toolset): $(if ($wdkInstalled) { 'found' } else { 'NOT found' })"
Write-Host ""

if ($DriverOnly) {
    if (-not $wdkInstalled) {
        throw "The WDK is not installed, so the driver cannot be built. See README.md, 'Requirements'."
    }

    Invoke-MSBuild -MSBuildPath $msbuild -ProjectPath (Join-Path $root 'driver\PanelDisplayDriver.vcxproj') -Label 'PanelDisplayDriver'
    Write-Host "`nDriver build succeeded." -ForegroundColor Green
    exit 0
}

Invoke-MSBuild -MSBuildPath $msbuild -ProjectPath (Join-Path $root 'tests\PanelDisplayDriver.Tests.vcxproj') -Label 'PanelDisplayDriver.Tests'
Invoke-MSBuild -MSBuildPath $msbuild -ProjectPath (Join-Path $root 'tools\PanelDisplayCtl\PanelDisplayCtl.vcxproj') -Label 'PanelDisplayCtl'

if ($wdkInstalled) {
    Invoke-MSBuild -MSBuildPath $msbuild -ProjectPath (Join-Path $root 'driver\PanelDisplayDriver.vcxproj') -Label 'PanelDisplayDriver'
} else {
    Write-Warning "Skipping the driver project: the WDK is not installed."
    Write-Warning "Install the Windows Driver Kit matching your Windows SDK, then re-run this script."
}

if (-not $SkipTests) {
    $testExe = Join-Path $root "tests\$platform\$Configuration\PanelDisplayDriver.Tests.exe"

    Write-Host "`n==> Running unit tests" -ForegroundColor Cyan
    & $testExe
    if ($LASTEXITCODE -ne 0) {
        throw "Unit tests failed."
    }
}

Write-Host "`nBuild complete." -ForegroundColor Green

if (-not $wdkInstalled) {
    Write-Host "Note: the driver binary was NOT produced (WDK missing)." -ForegroundColor Yellow
}
