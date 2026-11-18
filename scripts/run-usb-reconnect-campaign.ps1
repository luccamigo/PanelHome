<#
.SYNOPSIS
    Runs consecutive native-USB reconnect cycles against a physically attached
    tablet and records whether each one establishes a PLMX v2 session.

.DESCRIPTION
    Each cycle starts a fresh PanelHost against a device that is already in
    accessory mode. That is precisely the case PLMX v1 could not survive: the
    previous session's bytes are still in the pipe, and a session with no
    identity decoded them as its own. A cycle counts as a success only when the
    host reports an established session and a completed round trip.

    The Android app stays running across cycles, so it also exercises the
    accessory-side path where a new HELLO supersedes the session already in
    force without reopening the accessory descriptor.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 500)]
    [int]$Cycles = 30,

    # Zero means reconnect as fast as the host can be restarted, which is the
    # case most likely to leave residual bytes in the pipe.
    [ValidateRange(0, 60)]
    [int]$DelaySeconds = 0,

    [ValidateRange(0, 120)]
    [int]$PrepareSeconds = 6,

    [string]$OutputDirectory,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

function Invoke-Adb {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $adb @Arguments 2>&1 | Out-Null }
    finally { $ErrorActionPreference = $previous }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot '.artifacts\benchmarks\transport\reconnect'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$sdkLine = Get-Content -LiteralPath (Join-Path $repositoryRoot 'local.properties') |
    Where-Object { $_ -match '^sdk.dir=' } | Select-Object -First 1
if (-not $sdkLine) { throw 'local.properties does not define sdk.dir.' }
$sdkPath = ($sdkLine -replace '^sdk.dir=', '') -replace '\\:', ':' -replace '\\', '\'
$adb = Join-Path $sdkPath 'platform-tools\adb.exe'
if (-not (Test-Path -LiteralPath $adb)) { throw "adb was not found under sdk.dir: $adb" }

Push-Location $repositoryRoot
try {
    if (-not $SkipBuild) {
        & .\gradlew.bat assembleDebug
        if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }
        dotnet build windows\PanelHost\PanelHost.csproj `
            -p:UseAppHost=false `
            -p:OutputPath="$repositoryRoot\.benchmark-build\PanelHost\"
        if ($LASTEXITCODE -ne 0) { throw 'PanelHost build failed.' }
    }

    Invoke-Adb start-server
    $deviceRows = @(& $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\S' })
    if ($deviceRows.Count -ne 1 -or $deviceRows[0] -notmatch "\tdevice$") {
        throw "Exactly one authorized Android target must be connected. adb reported $($deviceRows.Count) row(s)."
    }
    & $adb install -r (Join-Path $repositoryRoot 'app\build\outputs\apk\debug\app-debug.apk') | Out-Null
    Invoke-Adb logcat -c
    Invoke-Adb shell am force-stop com.panelos.panelhome
    & $adb shell am start -n com.panelos.panelhome/.MainActivity `
        --es transport usb `
        --ez transportBenchmark true `
        --el transportBenchmarkDelayMillis 1000 | Out-Null
    Write-Host "PanelHome launched; waiting $PrepareSeconds s before the first cycle."
    Start-Sleep -Seconds $PrepareSeconds

    # The host owns the AOA transition, so adb must release the interface first.
    Invoke-Adb kill-server

    $hostDll = Join-Path $repositoryRoot '.benchmark-build\PanelHost\PanelHost.dll'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $csvPath = Join-Path $OutputDirectory "$stamp-reconnect-campaign.csv"
    $results = @()

    for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
        $cycleLog = Join-Path $OutputDirectory "$stamp-cycle-$('{0:D3}' -f $cycle).log"
        $cycleErr = "$cycleLog.err"
        $arguments = @(
            $hostDll,
            '--transport', 'usb',
            '--transport-benchmark',
            '--benchmark-output', $OutputDirectory,
            '--benchmark-scenario', 'dashboard',
            '--benchmark-samples', '5',
            '--benchmark-warmup', '1',
            '--benchmark-repetitions', '1',
            '--benchmark-idle-seconds', '0'
        )
        $started = Get-Date
        $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden `
            -RedirectStandardOutput $cycleLog -RedirectStandardError $cycleErr
        $elapsed = (Get-Date) - $started

        $log = Get-Content -LiteralPath $cycleLog -Raw -ErrorAction SilentlyContinue
        $established = $log -match '\[PLMX\] session established role=Host session=([0-9A-F]{6}\.\.\.)'
        $sessionPrefix = if ($established) { $Matches[1] } else { '<none>' }
        $completed = $log -match '\[Benchmark\] Completed'
        $resyncBytes = 0
        if ($log -match 'discarded=(\d+) bytes') { $resyncBytes = [int]$Matches[1] }
        $staleFrames = 0
        if ($log -match 'staleFrames=(\d+)') { $staleFrames = [int]$Matches[1] }
        $success = $established -and $completed -and $process.ExitCode -eq 0

        $results += [pscustomobject]@{
            cycle              = $cycle
            success            = $success
            sessionPrefix      = $sessionPrefix
            timeToEstablishedS = [math]::Round($elapsed.TotalSeconds, 3)
            resyncDiscarded    = $resyncBytes
            staleFrames        = $staleFrames
            exitCode           = $process.ExitCode
        }
        $status = if ($success) { 'ok' } else { 'FAIL' }
        Write-Host ("cycle {0,3}/{1}  {2,-4}  session={3}  {4,6:N2}s  resync={5}B stale={6}" -f `
            $cycle, $Cycles, $status, $sessionPrefix, $elapsed.TotalSeconds, $resyncBytes, $staleFrames)

        if ($DelaySeconds -gt 0) { Start-Sleep -Seconds $DelaySeconds }
    }

    $results | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding utf8
    $succeeded = @($results | Where-Object { $_.success }).Count
    $uniqueSessions = @($results | Where-Object { $_.sessionPrefix -ne '<none>' } |
        Select-Object -ExpandProperty sessionPrefix -Unique).Count
    Write-Host ''
    Write-Host "Reconnect campaign: $succeeded/$Cycles succeeded."
    Write-Host "Distinct session ids: $uniqueSessions (expected $succeeded)."
    Write-Host "Results: $csvPath"
    if ($succeeded -ne $Cycles) { Write-Warning 'At least one reconnect cycle failed.' }
}
finally {
    Invoke-Adb start-server
    Pop-Location
}
