[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('adb', 'usb')]
    [string]$Transport,

    [ValidateSet('dashboard', 'desktop-static', 'desktop-dynamic')]
    [string]$Scenario = 'dashboard',

    [ValidateRange(1, 100000)]
    [int]$Samples = 2000,

    [ValidateRange(0, 10000)]
    [int]$WarmupSamples = 100,

    [ValidateRange(1, 20)]
    [int]$Repetitions = 3,

    [ValidateRange(0, 120)]
    [int]$PrepareSeconds = 10,

    [ValidateRange(0, 120)]
    [int]$IdleSeconds = 10,

    [ValidateRange(512, 4194304)]
    [int]$FragmentBytes = 0,

    [ValidateRange(512, 4194304)]
    [int]$ReadBufferBytes = 0,

    [string]$OutputDirectory,

    [switch]$SkipBuild,
    [switch]$SkipInstall,

    # Desktop scenarios need PanelDisplayDriver, which PanelHost can only reach
    # when elevated. Elevation is opt-in and shows a UAC prompt.
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$benchmarkPorts = @(8765, 8766, 8767)

function Test-IsElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if ($Scenario -ne 'dashboard' -and -not $Elevated -and -not (Test-IsElevated)) {
    throw ("Scenario '$Scenario' captures the real Desktop through PanelDisplayDriver, which requires an " +
        'elevated PanelHost. Re-run with -Elevated (a UAC prompt appears), or start this script from an ' +
        'elevated shell. Nothing was measured.')
}

function Invoke-Adb {
    # adb writes its daemon banner to stderr. With $ErrorActionPreference =
    # 'Stop' PowerShell turns that into a terminating NativeCommandError, and
    # routing through cmd instead makes cmd wait on the daemon's inherited
    # handle forever. Relaxing the preference for the call is the only shape
    # that neither throws nor hangs.
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $adb @Arguments 2>&1 | Out-Null }
    finally { $ErrorActionPreference = $previous }
}

function Assert-IsolatedBenchmarkHost {
    $managedProcesses = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -ieq 'PanelHost.exe' -or
        $_.Name -match '(?i)ControlCenter' -or
        $_.CommandLine -match '(?i)PanelHost(?:\\|/)PanelHost\.dll|PanelHost\.dll|PanelOS\.ControlCenter'
    })
    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object {
        $_.LocalPort -in $benchmarkPorts
    })
    if ($managedProcesses.Count -gt 0 -or $listeners.Count -gt 0) {
        $processSummary = ($managedProcesses | ForEach-Object { "$($_.Name) PID=$($_.ProcessId)" }) -join ', '
        $portSummary = ($listeners | ForEach-Object { "tcp:$($_.LocalPort) PID=$($_.OwningProcess)" }) -join ', '
        throw "Close/stop the managed PanelHost before benchmarking. Existing processes: [$processSummary]. Listening benchmark ports: [$portSummary]. Nothing was terminated."
    }
}

Assert-IsolatedBenchmarkHost
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot '.artifacts\benchmarks\transport'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$localProperties = Join-Path $repositoryRoot 'local.properties'
$sdkLine = Get-Content -LiteralPath $localProperties |
    Where-Object { $_ -match '^sdk.dir=' } |
    Select-Object -First 1
if (-not $sdkLine) {
    throw 'local.properties does not define sdk.dir.'
}
$sdkPath = ($sdkLine -replace '^sdk.dir=', '') -replace '\\:', ':' -replace '\\', '\'
$adb = Join-Path $sdkPath 'platform-tools\adb.exe'
if (-not (Test-Path -LiteralPath $adb)) {
    throw "adb was not found under sdk.dir: $adb"
}

Push-Location $repositoryRoot
$adbServerStopped = $false
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
        throw "Exactly one authorized Android target must be connected. adb reported $($deviceRows.Count) target row(s)."
    }
    if (-not $SkipInstall) {
        & $adb install -r (Join-Path $repositoryRoot 'app\build\outputs\apk\debug\app-debug.apk')
        if ($LASTEXITCODE -ne 0) { throw 'APK installation failed.' }
    }

    if ($Transport -eq 'adb') {
        $sessionTokenBytes = [byte[]]::new(32)
        [Security.Cryptography.RandomNumberGenerator]::Fill($sessionTokenBytes)
        $sessionToken = [Convert]::ToHexString($sessionTokenBytes)
        [Array]::Clear($sessionTokenBytes, 0, $sessionTokenBytes.Length)
        $env:PANELOS_ADB_AUTH_TOKEN = $sessionToken
        foreach ($port in 8765, 8766, 8767) {
            & $adb reverse "tcp:$port" "tcp:$port" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "adb reverse failed for tcp:$port." }
        }
    } else {
        Remove-Item Env:\PANELOS_ADB_AUTH_TOKEN -ErrorAction SilentlyContinue
    }

    & $adb logcat -c
    $delayMillis = $PrepareSeconds * 1000
    & $adb shell am force-stop com.panelos.panelhome
    $appLaunchArguments = @(
        'shell', 'am', 'start', '-n', 'com.panelos.panelhome/.MainActivity',
        '--es', 'transport', $Transport,
        '--ez', 'transportBenchmark', 'true',
        '--el', 'transportBenchmarkDelayMillis', $delayMillis
    )
    if ($Transport -eq 'adb') {
        $appLaunchArguments += @('--es', 'panelAdbAuthToken', $sessionToken)
    }
    & $adb @appLaunchArguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'PanelHome launch failed.' }

    Write-Host "PanelHome is ready. Prepare scenario '$Scenario' on the tablet within $PrepareSeconds seconds."
    if ($Scenario -ne 'dashboard') {
        Write-Host 'Open Desktop on the tablet; keep the same 1280x800 H.264/60 FPS/10 Mbps workload for both transports.'
    }

    if ($Transport -eq 'usb') {
        # The OEM ADB daemon commonly holds the same WinUSB interface used to
        # send AOA control requests. AOA+ADB re-enumerates ADB after transition.
        & $adb kill-server | Out-Null
        $adbServerStopped = $true
    }

    $hostDll = Join-Path $repositoryRoot '.benchmark-build\PanelHost\PanelHost.dll'
    if ($SkipBuild -and -not (Test-Path -LiteralPath $hostDll)) {
        throw "Benchmark PanelHost build not found: $hostDll"
    }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $hostLog = Join-Path $OutputDirectory "$stamp-$Transport-$Scenario-host.log"
    $hostError = Join-Path $OutputDirectory "$stamp-$Transport-$Scenario-host.err.log"
    $before = @(Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.json' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    $arguments = @(
        $hostDll,
        '--transport', $Transport,
        '--transport-benchmark',
        '--benchmark-output', $OutputDirectory,
        '--benchmark-scenario', $Scenario,
        '--benchmark-samples', $Samples,
        '--benchmark-warmup', $WarmupSamples,
        '--benchmark-repetitions', $Repetitions,
        '--benchmark-idle-seconds', $IdleSeconds,
        '--video-codec', 'h264',
        '--video-target-fps', '60'
    )
    # Carrier tunables travel as environment variables so a buffer or fragment
    # sweep runs against the same binary that produced the other rows.
    if ($FragmentBytes -gt 0) { $env:PANELOS_PLMX_FRAGMENT = $FragmentBytes }
    else { Remove-Item Env:\PANELOS_PLMX_FRAGMENT -ErrorAction SilentlyContinue }
    if ($ReadBufferBytes -gt 0) { $env:PANELOS_USB_READ_BUFFER = $ReadBufferBytes }
    else { Remove-Item Env:\PANELOS_USB_READ_BUFFER -ErrorAction SilentlyContinue }
    $tapJob = $null
    if ($Scenario -ne 'dashboard') {
        $tapJob = Start-Job -ScriptBlock {
            param($adbPath, $transportName)
            Start-Sleep -Seconds 3
            if ($transportName -eq 'usb') { & $adbPath start-server | Out-Null }
            $deadline = (Get-Date).AddSeconds(20)
            while ((Get-Date) -lt $deadline -and (& $adbPath get-state 2>$null) -ne 'device') {
                Start-Sleep -Milliseconds 500
            }
            if ((& $adbPath get-state 2>$null) -ne 'device') {
                throw 'ADB MI_01 did not become available for the benchmark Desktop tap.'
            }
            & $adbPath shell input tap 996 660 | Out-Null
        } -ArgumentList $adb, $Transport
    }
    if ($Elevated -and -not (Test-IsElevated)) {
        # RunAs cannot redirect standard streams, so the elevated host writes
        # its own log through cmd instead of losing the output entirely.
        $quoted = ($arguments | ForEach-Object { '"{0}"' -f $_ }) -join ' '
        $benchmarkProcess = Start-Process -FilePath 'cmd.exe' -Verb RunAs -PassThru -Wait `
            -ArgumentList @('/c', "dotnet $quoted > `"$hostLog`" 2> `"$hostError`"")
    } else {
        $benchmarkProcess = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden `
            -RedirectStandardOutput $hostLog -RedirectStandardError $hostError
    }
    $benchmarkHostPid = $benchmarkProcess.Id
    Write-Host "Isolated benchmark PanelHost PID: $benchmarkHostPid"
    if ($tapJob) {
        Receive-Job -Job $tapJob -Wait -AutoRemoveJob | Out-Null
    }
    $current = @(Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.json' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    $result = $current | Where-Object { $_ -notin $before } | Select-Object -First 1
    if ($benchmarkProcess.ExitCode -ne 0 -or -not $result) {
        $details = Get-Content -LiteralPath $hostError -Raw -ErrorAction SilentlyContinue
        if (-not $details) { $details = Get-Content -LiteralPath $hostLog -Tail 30 -ErrorAction SilentlyContinue }
        throw "Benchmark host exited with code $($benchmarkProcess.ExitCode) without a valid JSON result. Host output: $details"
    }
    Write-Host "Benchmark result: $result"

    Invoke-Adb start-server
    if ((& $adb get-state 2>$null) -eq 'device') {
        & $adb logcat -d -v threadtime PanelOS:I '*:S' |
            Set-Content -LiteralPath (Join-Path $OutputDirectory "$stamp-$Transport-$Scenario-android.log") -Encoding utf8
        $hardware = [ordered]@{
            capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            androidModel = (& $adb shell getprop ro.product.model).Trim()
            androidVersion = (& $adb shell getprop ro.build.version.release).Trim()
            windowsVersion = [System.Environment]::OSVersion.VersionString
            logicalProcessors = [System.Environment]::ProcessorCount
            resolution = '1280x800'
            codec = 'H.264'
            preferredFps = 60
            bitrateBitsPerSecond = 10000000
            panelHostPid = $benchmarkHostPid
            transport = $Transport
            scenario = $Scenario
        }
        $hardware | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory "$stamp-hardware.json") -Encoding utf8
    }
}
finally {
    Remove-Item Env:\PANELOS_ADB_AUTH_TOKEN -ErrorAction SilentlyContinue
    if ($adbServerStopped) {
        Invoke-Adb start-server
    }
    Pop-Location
}
