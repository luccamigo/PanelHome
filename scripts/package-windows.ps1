param(
    [Parameter(Mandatory = $true)]
    [string]$OutputZip,

    # Public releases must not redistribute the PawnIO installer: no download,
    # no hash check, no EXE and no redistribution notes; PawnIO.txt only links
    # to the official release.
    [switch]$ExcludePawnIO
)

$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputZip))
if ([IO.Path]::GetExtension($output) -ne '.zip' -or (Test-Path -LiteralPath $output)) {
    throw 'Choose a new .zip output path.'
}

$repo = Split-Path -Parent $PSScriptRoot
$outputDirectory = [IO.Path]::GetDirectoryName($output)
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$staging = Join-Path $outputDirectory ([Guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging 'payload'
$package = Join-Path $staging 'PanelOS.zip'
$prerequisites = Join-Path $payload 'prerequisites'

Push-Location -LiteralPath $repo
try {
    New-Item -ItemType Directory -Path $prerequisites | Out-Null
    $installer = Join-Path $prerequisites 'PawnIO_setup.exe'
    $releasePage = 'https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0'
    $url = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe'
    $expectedHash = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
    if (-not $ExcludePawnIO) {
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $installer
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $installer).Hash -ne $expectedHash) {
            throw 'The PawnIO 2.2.0 installer does not match the official SHA-256.'
        }
    }

    foreach ($component in @('PanelOS.ControlCenter', 'PanelHost')) {
        dotnet build "windows/$component/$component.csproj" -c Release
        if ($LASTEXITCODE -ne 0) { throw "Release build failed for $component." }

        $buildDirectory = Join-Path $repo "windows\$component\bin\Release\net8.0-windows10.0.19041.0"
        Get-ChildItem -LiteralPath $buildDirectory | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $payload -Recurse -Force
        }
    }

    # The payload supports Windows only. Keep every Windows architecture used by native dependencies.
    $runtimeRoot = Join-Path $payload 'runtimes'
    if (Test-Path -LiteralPath $runtimeRoot) {
        Get-ChildItem -LiteralPath $runtimeRoot -Directory | Where-Object { $_.Name -notmatch '^win(?:[0-9]+)?(?:-|$)' } | ForEach-Object {
            $runtimePath = [IO.Path]::GetFullPath($_.FullName)
            if ([IO.Path]::GetDirectoryName($runtimePath) -ne [IO.Path]::GetFullPath($runtimeRoot)) { throw 'Unexpected runtime path.' }
            Remove-Item -LiteralPath $runtimePath -Recurse -Force
        }
    }
    if ($ExcludePawnIO) {
        @"
PanelOS reads hardware sensors (such as CPU temperature) through PawnIO, which is NOT included in this package.
To enable these readings, install PawnIO 2.2.0 from the official release page:
$releasePage
The installer asks for Windows administrator approval. PanelOS works without it, without those readings.
"@ | Set-Content -LiteralPath (Join-Path $prerequisites 'PawnIO.txt') -Encoding UTF8
    } else {
        Copy-Item -LiteralPath (Join-Path $repo 'docs\pawnio-redistribution.md') -Destination $prerequisites
        @"
PawnIO 2.2.0 supports hardware sensor readings in PanelOS.
To install it, run PawnIO_setup.exe and choose the official edition if prompted.
The installer asks for Windows administrator approval.
Source: $url
SHA-256: $expectedHash
"@ | Set-Content -LiteralPath (Join-Path $prerequisites 'PawnIO.txt') -Encoding UTF8
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $package)
    [IO.File]::Move($package, $output)
    Write-Output $output
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $staging) {
        $resolvedStaging = (Resolve-Path -LiteralPath $staging).ProviderPath
        if ($resolvedStaging -eq $staging -and [IO.Path]::GetDirectoryName($resolvedStaging) -eq $outputDirectory) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    }
}
