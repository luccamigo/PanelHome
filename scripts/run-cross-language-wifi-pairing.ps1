<#
.SYNOPSIS
    Pairs the Kotlin production client against the C# production pairing session, over loopback (QR and typed code).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repositoryRoot ("build\cross-language-wifi-pairing-" + [guid]::NewGuid().ToString('n'))
$runRoot = [IO.Path]::GetFullPath($runRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'build'))
if (-not $runRoot.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar)) {
    throw "Refusing to use unexpected harness directory: $runRoot"
}
New-Item -ItemType Directory -Path $runRoot | Out-Null

$manifestPath = Join-Path $runRoot 'wifi-pairing.json'
$hostOut = Join-Path $runRoot 'host.stdout.log'
$hostErr = Join-Path $runRoot 'host.stderr.log'
$previousManifest = $env:PANELOS_WIFI_LIVE_MANIFEST
$env:PANELOS_WIFI_LIVE_MANIFEST = $manifestPath
$hostProcess = $null
$clientExit = 1
try {
    $hostProcess = Start-Process `
        -FilePath 'dotnet' `
        -ArgumentList @(
            'test',
            'windows/PanelHost.Tests/PanelHost.Tests.csproj',
            '--filter',
            'TestCategory=CrossLanguageWifiHost',
            '--logger',
            'console;verbosity=detailed',
            '--nologo'
        ) `
        -WorkingDirectory $repositoryRoot `
        -RedirectStandardOutput $hostOut `
        -RedirectStandardError $hostErr `
        -WindowStyle Hidden `
        -PassThru

    $readyDeadline = [DateTime]::UtcNow.AddSeconds(45)
    while (-not (Test-Path -LiteralPath $manifestPath)) {
        if ($hostProcess.HasExited) {
            throw "C# WiFi pairing host exited before publishing its manifest (exit $($hostProcess.ExitCode))."
        }
        if ([DateTime]::UtcNow -ge $readyDeadline) {
            throw 'Timed out waiting for the C# WiFi pairing host manifest.'
        }
        Start-Sleep -Milliseconds 100
    }

    & (Join-Path $repositoryRoot 'gradlew.bat') `
        testDebugUnitTest `
        --tests 'com.panelos.panelhome.integration.CrossLanguageWifiPairingTest' `
        --rerun-tasks
    $clientExit = $LASTEXITCODE
}
finally {
    New-Item -ItemType File -Path ($manifestPath + '.done') -Force | Out-Null
    if ($hostProcess -ne $null) {
        if (-not $hostProcess.WaitForExit(30000)) {
            Stop-Process -Id $hostProcess.Id -Force
            throw 'C# WiFi pairing host did not stop after the Kotlin client completed.'
        }
        # Drain redirected async streams before printing the logs below.
        $hostProcess.WaitForExit()
        $hostProcess.Refresh()
    }
    if ($null -eq $previousManifest) {
        Remove-Item Env:PANELOS_WIFI_LIVE_MANIFEST -ErrorAction SilentlyContinue
    } else {
        $env:PANELOS_WIFI_LIVE_MANIFEST = $previousManifest
    }
    if (Test-Path -LiteralPath $hostOut) { Get-Content -LiteralPath $hostOut }
    if (Test-Path -LiteralPath $hostErr) { Get-Content -LiteralPath $hostErr }
}

if ($clientExit -ne 0) {
    throw "Kotlin WiFi pairing tests failed with exit code $clientExit. Logs: $runRoot"
}
if (-not (Test-Path -LiteralPath ($manifestPath + '.host-passed'))) {
    throw "C# WiFi pairing host did not publish its success marker. Logs: $runRoot"
}

Write-Host 'Cross-language WiFi pairing passed (C# production pairing session <-> Kotlin production client).'
