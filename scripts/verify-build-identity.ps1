<#
.SYNOPSIS
    Prints host/APK build identity and protocol metadata and warns about drift.
#>
[CmdletBinding()]
param(
    [string] $HostMetadata = 'windows/PanelHost/bin/Debug/net8.0-windows10.0.19041.0/panelhost-build-identity.json',
    [string] $Apk = 'app/build/outputs/apk/debug/app-debug.apk',
    [string] $IntendedApkBuildIdentity
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$hostPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $HostMetadata))
$apkPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Apk))
if (-not (Test-Path -LiteralPath $hostPath)) { throw "Host metadata not found: $hostPath" }
if (-not (Test-Path -LiteralPath $apkPath)) { throw "APK not found: $apkPath" }

$hostIdentity = Get-Content -LiteralPath $hostPath -Raw | ConvertFrom-Json
$apkAnalyzer = Get-Command 'apkanalyzer.bat' -ErrorAction SilentlyContinue
if ($null -eq $apkAnalyzer) { $apkAnalyzer = Get-Command 'apkanalyzer' -ErrorAction SilentlyContinue }
if ($null -ne $apkAnalyzer) {
    [xml] $manifest = & $apkAnalyzer.Source manifest print $apkPath
    if ($LASTEXITCODE -ne 0) { throw "apkanalyzer failed for $apkPath" }
    $androidNamespace = 'http://schemas.android.com/apk/res/android'
    function Get-ApkMetadata([string] $name) {
        $node = $manifest.manifest.application.'meta-data' |
            Where-Object { $_.GetAttribute('name', $androidNamespace) -eq $name } |
            Select-Object -First 1
        if ($null -eq $node) { throw "APK metadata '$name' was not found." }
        return $node.GetAttribute('value', $androidNamespace)
    }
} else {
    # build-tools always ships aapt2, while apkanalyzer is optional in local SDKs.
    $sdkCandidates = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $localProperties = Join-Path $repositoryRoot 'local.properties'
    if (Test-Path -LiteralPath $localProperties) {
        $sdkLine = Get-Content -LiteralPath $localProperties |
            Where-Object { $_ -like 'sdk.dir=*' } |
            Select-Object -First 1
        if ($null -ne $sdkLine) {
            $sdkCandidates += $sdkLine.Substring('sdk.dir='.Length).Replace('\:', ':').Replace('\\', '\')
        }
    }
    $aapt2 = $sdkCandidates |
        ForEach-Object {
            Get-ChildItem -LiteralPath (Join-Path $_ 'build-tools') -Recurse `
                -Filter 'aapt2*' -ErrorAction SilentlyContinue
        } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $aapt2) {
        throw 'Neither apkanalyzer nor aapt2 was found in the Android SDK.'
    }
    $manifestTree = @(& $aapt2.FullName dump xmltree $apkPath --file AndroidManifest.xml)
    if ($LASTEXITCODE -ne 0) { throw "aapt2 failed for $apkPath" }
    function Get-ApkMetadata([string] $name) {
        for ($index = 0; $index -lt $manifestTree.Count; $index++) {
            if ($manifestTree[$index] -notlike "*$name*") { continue }
            for ($valueIndex = $index + 1;
                 $valueIndex -lt [Math]::Min($index + 4, $manifestTree.Count);
                 $valueIndex++) {
                if ($manifestTree[$valueIndex] -match 'android:value[^=]*=(?:"([^"]*)"|([^\s]+))') {
                    return $(if ($matches[1]) { $matches[1] } else { $matches[2] })
                }
            }
        }
        throw "APK metadata '$name' was not found."
    }
}

$apkIdentity = Get-ApkMetadata 'com.panelos.panelhome.BUILD_IDENTITY'
$apkBuildNumber = [long](Get-ApkMetadata 'com.panelos.panelhome.BUILD_NUMBER')
$apkProtocol = [int](Get-ApkMetadata 'com.panelos.panelhome.PROTOCOL_VERSION')

Write-Host "Host build identity: $($hostIdentity.buildIdentity)"
Write-Host "Host build number:   $($hostIdentity.buildNumber)"
Write-Host "Host protocol:       $($hostIdentity.protocolVersion)"
Write-Host "APK build identity:  $apkIdentity"
Write-Host "APK build number:    $apkBuildNumber"
Write-Host "APK protocol:        $apkProtocol"

if ($hostIdentity.buildNumber -gt $apkBuildNumber) {
    Write-Warning (
        "Host build $($hostIdentity.buildNumber) is newer than APK build $apkBuildNumber. " +
        'The protocol still decides compatibility; verify that this is the APK intended for validation.')
}
if ($hostIdentity.protocolVersion -ne $apkProtocol) {
    Write-Warning 'Host and APK protocol versions differ; a live handshake is expected to reject them.'
}
if (-not [string]::IsNullOrWhiteSpace($IntendedApkBuildIdentity) -and
    $apkIdentity -ne $IntendedApkBuildIdentity) {
    Write-Warning (
        "Installed/selected APK identity '$apkIdentity' differs from intended build " +
        "'$IntendedApkBuildIdentity'.")
}
