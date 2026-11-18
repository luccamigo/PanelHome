<#
.SYNOPSIS
    Validates Windows solution completeness and DriverInterop ownership.

.DESCRIPTION
    The CI job builds and tests the solution, so a project missing from it is
    never compiled and never run - silently, and only on CI. That already
    happened once with PanelHost.SensorProbe, which was added to the tree and
    to the workflow's hand-maintained project list separately.

    The driver is deliberately out of scope: driver/PanelDisplayDriver has its
    own C++ solution and needs the WDK toolchain.

    DriverInterop is a standalone assembly. A Compile source-link targeting a
    DriverInterop directory would compile its types into another assembly and
    reintroduce the duplicate-type failure this script guards against.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$windowsRoot = Join-Path $repositoryRoot 'windows'
$solutionPath = Join-Path $windowsRoot 'PanelOS.sln'

if (-not (Test-Path -LiteralPath $solutionPath)) {
    throw "windows/PanelOS.sln was not found at $solutionPath."
}

# Paths as the solution stores them: relative to windows/, backslash separated.
$onDisk = Get-ChildItem -LiteralPath $windowsRoot -Recurse -Filter '*.csproj' |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object { $_.FullName.Substring($windowsRoot.Length + 1) } |
    Sort-Object

$inSolution = & dotnet sln $solutionPath list |
    Where-Object { $_ -like '*.csproj' } |
    ForEach-Object { $_.Trim() } |
    Sort-Object

if ($LASTEXITCODE -ne 0) {
    throw "dotnet sln list failed with exit code $LASTEXITCODE."
}

$missing = $onDisk | Where-Object { $inSolution -notcontains $_ }
$stale = $inSolution | Where-Object { $onDisk -notcontains $_ }

foreach ($project in $missing) {
    Write-Host "MISSING from the solution: windows/$project"
}
foreach ($project in $stale) {
    Write-Host "LISTED but not on disk:    windows/$project"
}

if ($missing.Count -gt 0 -or $stale.Count -gt 0) {
    throw (
        "windows/PanelOS.sln does not match the projects on disk. " +
        "Add a new project with: dotnet sln windows/PanelOS.sln add <path>")
}

$driverInteropSourceLinks = Get-ChildItem -LiteralPath $windowsRoot -Recurse `
        -Filter '*.csproj' |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-String -Pattern '<Compile\s+Include\s*=\s*["''][^"'']*DriverInterop[\\/][^"'']*["'']'

foreach ($sourceLink in $driverInteropSourceLinks) {
    Write-Host (
        "FORBIDDEN DriverInterop source-link: {0}:{1}" -f
        $sourceLink.Path,
        $sourceLink.LineNumber)
}

if ($driverInteropSourceLinks.Count -gt 0) {
    throw (
        'DriverInterop sources must be consumed through a ProjectReference ' +
        'to PanelOS.DriverInterop, never through Compile Include.')
}

Write-Host "windows/PanelOS.sln matches all $($onDisk.Count) projects on disk."
Write-Host 'No project compiles DriverInterop sources through Compile Include.'
