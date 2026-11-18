[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

function Find-Adb {
    $pathCommand = Get-Command adb.exe -ErrorAction SilentlyContinue
    if ($null -ne $pathCommand) {
        return $pathCommand.Source
    }

    $sdkRoots = [System.Collections.Generic.List[string]]::new()
    foreach ($environmentVariable in @("ANDROID_SDK_ROOT", "ANDROID_HOME")) {
        $sdkRoot = [Environment]::GetEnvironmentVariable($environmentVariable)
        if (-not [string]::IsNullOrWhiteSpace($sdkRoot)) {
            $sdkRoots.Add($sdkRoot)
        }
    }

    $localPropertiesPath = Join-Path $script:RepositoryRoot "local.properties"
    if (Test-Path -LiteralPath $localPropertiesPath) {
        $sdkProperty = Get-Content -LiteralPath $localPropertiesPath |
            Where-Object { $_ -match '^\s*sdk\.dir\s*=' } |
            Select-Object -First 1

        if ($null -ne $sdkProperty -and $sdkProperty -match '^\s*sdk\.dir\s*=\s*(.+?)\s*$') {
            $sdkFromProperties = $Matches[1] -replace '\\:', ':' -replace '\\\\', '\'
            if (-not [string]::IsNullOrWhiteSpace($sdkFromProperties)) {
                $sdkRoots.Add($sdkFromProperties)
            }
        }
    }

    foreach ($sdkRoot in ($sdkRoots | Select-Object -Unique)) {
        $candidate = Join-Path $sdkRoot "platform-tools\adb.exe"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

$RepositoryRoot = Split-Path -Parent $PSScriptRoot
$script:RepositoryRoot = $RepositoryRoot
$adbPath = Find-Adb

if ([string]::IsNullOrWhiteSpace($adbPath)) {
    Write-Error "ADB não encontrado. Instale o Android SDK Platform-Tools ou configure ANDROID_SDK_ROOT/ANDROID_HOME."
    exit 1
}

Write-Host "[PanelHome] ADB: $adbPath"
Write-Host "[PanelHome] Verificando dispositivos USB..."

$adbOutput = @(& $adbPath devices)
if ($LASTEXITCODE -ne 0) {
    Write-Error "Falha ao executar 'adb devices'."
    exit 1
}

$devices = @(
    foreach ($line in $adbOutput) {
        $lineText = $line.ToString().Trim()
        if ($lineText -match '^(\S+)\s+(device|unauthorized|offline|no permissions)$') {
            [pscustomobject]@{
                Serial = $Matches[1]
                State = $Matches[2]
            }
        }
    }
)

if ($devices.Count -eq 0) {
    Write-Host "Nenhum dispositivo Android conectado."
    exit 1
}

$authorizedDevices = @($devices | Where-Object { $_.State -eq "device" })
$unauthorizedDevices = @($devices | Where-Object { $_.State -eq "unauthorized" })

if ($authorizedDevices.Count -eq 0 -and $unauthorizedDevices.Count -gt 0) {
    Write-Host "Dispositivo conectado, mas ADB não autorizado."
    exit 1
}

if ($devices.Count -ne 1) {
    Write-Error "Mais de um dispositivo Android detectado. Mantenha exatamente um dispositivo conectado e autorizado."
    exit 1
}

if ($authorizedDevices.Count -eq 0) {
    $states = ($devices.State | Select-Object -Unique) -join ", "
    Write-Error "Nenhum dispositivo Android autorizado disponível (estado: $states)."
    exit 1
}

$deviceSerial = $authorizedDevices[0].Serial
if (
    $deviceSerial -match '^emulator-' -or
    $deviceSerial -match ':' -or
    $deviceSerial -match '(^adb-|_adb-tls-connect|\.local$)'
) {
    Write-Error "O alvo ADB não é um dispositivo USB físico. Emuladores e conexões ADB por rede não são permitidos."
    exit 1
}

Write-Host "[PanelHome] Dispositivo autorizado: $deviceSerial"
$sessionTokenBytes = [byte[]]::new(32)
[Security.Cryptography.RandomNumberGenerator]::Fill($sessionTokenBytes)
$sessionToken = [Convert]::ToHexString($sessionTokenBytes)
[Array]::Clear($sessionTokenBytes, 0, $sessionTokenBytes.Length)
$reversePorts = @(8765, 8766)
foreach ($port in $reversePorts) {
    Write-Host "[PanelHome] Configurando USB reverse tcp:$port -> tcp:$port..."

    & $adbPath -s $deviceSerial reverse "tcp:$port" "tcp:$port"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Não foi possível configurar o ADB reverse tcp:$port."
        exit 1
    }
}

$reverseOutput = @(& $adbPath -s $deviceSerial reverse --list)
if ($LASTEXITCODE -ne 0) {
    Write-Error "Não foi possível consultar os redirecionamentos ADB reverse."
    exit 1
}

$reverseList = $reverseOutput -join "`n"
foreach ($port in $reversePorts) {
    if ($reverseList -notmatch "tcp:$port\s+tcp:$port") {
        Write-Error "O ADB reverse tcp:$port tcp:$port não foi confirmado."
        exit 1
    }
}

Write-Host "[PanelHome] ADB reverse de controle (8765) e vídeo (8766) confirmado."

& $adbPath -s $deviceSerial shell am start -S `
    -n "com.panelos.panelhome/.MainActivity" `
    --es transport adb `
    --es panelAdbAuthToken $sessionToken | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Não foi possível iniciar PanelHome com a sessão autenticada."
    exit 1
}

$dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand) {
    Write-Error ".NET SDK não encontrado. Instale o SDK .NET 8."
    exit 1
}

$installedSdks = @(& $dotnetCommand.Source --list-sdks)
if ($LASTEXITCODE -ne 0 -or -not ($installedSdks -match '^8\.')) {
    Write-Error "O SDK .NET 8 é necessário e não foi encontrado."
    exit 1
}

$panelHostProject = Join-Path $RepositoryRoot "windows\PanelHost"
if (-not (Test-Path -LiteralPath $panelHostProject -PathType Container)) {
    Write-Error "Projeto PanelHost não encontrado em '$panelHostProject'."
    exit 1
}

Write-Host "[PanelHome] Iniciando PanelHost em 127.0.0.1:8765/8766. Pressione Ctrl+C para encerrar."

$panelHostExitCode = 0
Push-Location $RepositoryRoot
try {
    $env:PANELOS_ADB_AUTH_TOKEN = $sessionToken
    & $dotnetCommand.Source run --project "windows/PanelHost"
    $panelHostExitCode = $LASTEXITCODE
}
finally {
    Remove-Item Env:\PANELOS_ADB_AUTH_TOKEN -ErrorAction SilentlyContinue
    Pop-Location
}

exit $panelHostExitCode
