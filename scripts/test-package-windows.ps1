# Packages with -ExcludePawnIO and checks that the public package has no PawnIO installer.
$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ('panelos-pkg-test-' + [Guid]::NewGuid().ToString('N'))
$zip = Join-Path $work 'out\PanelOS-test-windows.zip'
$extract = Join-Path $work 'extracted'
try {
    & (Join-Path $PSScriptRoot 'package-windows.ps1') -OutputZip $zip -ExcludePawnIO | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $extract
    $failures = @()
    if (Get-ChildItem -LiteralPath $extract -Recurse -Filter 'PawnIO_setup*' -File) { $failures += 'PawnIO installer present' }
    if (Test-Path -LiteralPath (Join-Path $extract 'prerequisites\pawnio-redistribution.md')) { $failures += 'redistribution notes present' }
    $note = Join-Path $extract 'prerequisites\PawnIO.txt'
    if (-not (Test-Path -LiteralPath $note)) { $failures += 'PawnIO.txt missing' }
    else {
        $text = Get-Content -Raw -LiteralPath $note
        if ($text -notmatch [regex]::Escape('https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0')) { $failures += 'official link missing' }
        if ($text -match 'SHA-256') { $failures += 'PawnIO.txt still describes a bundled installer' }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $extract 'PanelHost.exe'))) { $failures += 'PanelHost.exe missing' }
    if ($failures) { throw ('package-windows -ExcludePawnIO failed: ' + ($failures -join '; ')) }
    Write-Output 'OK: public package contains no PawnIO installer and links to the official release.'
} finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
