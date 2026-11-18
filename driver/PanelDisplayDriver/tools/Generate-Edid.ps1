<#
.SYNOPSIS
    Generates the 128-byte EDID 1.4 block advertised by the PanelOS virtual monitor.

.DESCRIPTION
    The driver reports a monitor description of type IDDCX_MONITOR_DESCRIPTION_TYPE_EDID
    when it creates the virtual monitor. Rather than hand-typing 128 bytes, this script
    builds the block from readable parameters, verifies the EDID checksum, and emits the
    C++ array consumed by driver/Edid.h.

    Regenerate this when the PanelOS tablet's real panel characteristics are known, so the
    preferred detailed timing matches the physical device.

.EXAMPLE
    pwsh -File tools/Generate-Edid.ps1 -Verify
    pwsh -File tools/Generate-Edid.ps1 -EmitCpp
#>
[CmdletBinding()]
param(
    [switch]$EmitCpp,
    [switch]$Verify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Panel parameters. Preferred detailed timing is the tablet/stream-native
# 1280x800 @ 60 Hz (CVT-RB). Higher-resolution modes remain available.
# ---------------------------------------------------------------------------
$ManufacturerId   = 'PNL'   # 3 letters, encoded as 3 x 5-bit values
$ProductCode      = 0x0001
$SerialNumber     = 0x00000001
$ManufactureWeek  = 1
$ManufactureYear  = 2026
$MonitorName      = 'PanelOS'
$WidthMm          = 259
$HeightMm         = 162

function New-ManufacturerId([string]$Id) {
    if ($Id.Length -ne 3) { throw "Manufacturer id must be exactly 3 letters." }
    $packed = 0
    foreach ($ch in $Id.ToUpper().ToCharArray()) {
        $value = [int][char]$ch - [int][char]'A' + 1
        if ($value -lt 1 -or $value -gt 26) { throw "Manufacturer id must be A-Z." }
        $packed = ($packed -shl 5) -bor $value
    }
    # Big-endian, high bit of the first byte is reserved zero.
    return @([byte](($packed -shr 8) -band 0xFF), [byte]($packed -band 0xFF))
}

function New-DetailedTiming {
    param(
        [int]$PixelClockHz, [int]$HActive, [int]$HBlank, [int]$HSyncOffset, [int]$HSyncWidth,
        [int]$VActive, [int]$VBlank, [int]$VSyncOffset, [int]$VSyncWidth,
        [int]$WidthMm, [int]$HeightMm, [byte]$Flags
    )
    $clock = [int]($PixelClockHz / 10000)
    if ($clock -gt 0xFFFF) { throw "Pixel clock out of range for EDID detailed timing." }

    return [byte[]]@(
        ($clock -band 0xFF), (($clock -shr 8) -band 0xFF),
        ($HActive -band 0xFF),
        ($HBlank -band 0xFF),
        (((($HActive -shr 8) -band 0x0F) -shl 4) -bor (($HBlank -shr 8) -band 0x0F)),
        ($VActive -band 0xFF),
        ($VBlank -band 0xFF),
        (((($VActive -shr 8) -band 0x0F) -shl 4) -bor (($VBlank -shr 8) -band 0x0F)),
        ($HSyncOffset -band 0xFF),
        ($HSyncWidth -band 0xFF),
        (((($VSyncOffset -band 0x0F) -shl 4)) -bor ($VSyncWidth -band 0x0F)),
        ((((($HSyncOffset -shr 8) -band 0x03) -shl 6)) -bor
         (((($HSyncWidth -shr 8) -band 0x03) -shl 4)) -bor
         (((($VSyncOffset -shr 4) -band 0x03) -shl 2)) -bor
         (($VSyncWidth -shr 4) -band 0x03)),
        ($WidthMm -band 0xFF),
        ($HeightMm -band 0xFF),
        (((($WidthMm -shr 8) -band 0x0F) -shl 4) -bor (($HeightMm -shr 8) -band 0x0F)),
        0x00,   # horizontal border
        0x00,   # vertical border
        $Flags
    )
}

function New-TextDescriptor([byte]$Tag, [string]$Text) {
    $payload = [byte[]]@(0x00, 0x00, 0x00, $Tag, 0x00)
    $body = New-Object byte[] 13
    for ($i = 0; $i -lt 13; $i++) { $body[$i] = 0x20 }
    $chars = $Text.ToCharArray()
    if ($chars.Length -gt 12) { throw "Descriptor text must be 12 characters or fewer." }
    for ($i = 0; $i -lt $chars.Length; $i++) { $body[$i] = [byte][char]$chars[$i] }
    $body[$chars.Length] = 0x0A   # terminator required when text is shorter than 13
    return $payload + $body
}

$edid = New-Object System.Collections.Generic.List[byte]

# Header (bytes 0-7)
$edid.AddRange([byte[]]@(0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00))

# Vendor / product identification (bytes 8-17)
$edid.AddRange([byte[]](New-ManufacturerId $ManufacturerId))
$edid.AddRange([byte[]]@(($ProductCode -band 0xFF), (($ProductCode -shr 8) -band 0xFF)))
$edid.AddRange([byte[]]@(
    ($SerialNumber -band 0xFF), (($SerialNumber -shr 8) -band 0xFF),
    (($SerialNumber -shr 16) -band 0xFF), (($SerialNumber -shr 24) -band 0xFF)))
$edid.Add([byte]$ManufactureWeek)
$edid.Add([byte]($ManufactureYear - 1990))

# EDID version 1.4 (bytes 18-19)
$edid.AddRange([byte[]]@(0x01, 0x04))

# Basic display parameters (bytes 20-24)
#   0xA5 = digital input, 8 bits per colour, DisplayPort interface
#   0x0A = RGB 4:4:4, preferred timing describes the native pixel format
$edid.AddRange([byte[]]@(0xA5, [byte][math]::Round($WidthMm / 10), [byte][math]::Round($HeightMm / 10), 0x78, 0x0A))

# Chromaticity, standard sRGB primaries (bytes 25-34)
$edid.AddRange([byte[]]@(0xEE, 0x91, 0xA3, 0x54, 0x4C, 0x99, 0x26, 0x0F, 0x50, 0x54))

# Established timings: none, the driver reports its own mode list (bytes 35-37)
$edid.AddRange([byte[]]@(0x00, 0x00, 0x00))

# Standard timings (bytes 38-53). Aspect: 0b00 = 16:10, 0b11 = 16:9.
#   1280x800 @ 60, 1920x1080 @ 60, 1920x1200 @ 60, then unused slots.
$edid.AddRange([byte[]]@(0x9F, 0x00, 0xEF, 0xC0, 0xEF, 0x00))
$edid.AddRange([byte[]]@(0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01))

# Descriptor 1: preferred detailed timing, 1280x800 @ 60 Hz CVT reduced blanking.
# Flags 0x1A = digital separate sync, HSync positive, VSync negative.
$edid.AddRange([byte[]](New-DetailedTiming -PixelClockHz 71000000 `
    -HActive 1280 -HBlank 160 -HSyncOffset 48 -HSyncWidth 32 `
    -VActive 800 -VBlank 23 -VSyncOffset 3 -VSyncWidth 6 `
    -WidthMm $WidthMm -HeightMm $HeightMm -Flags 0x1A))

# Descriptor 2: monitor range limits (48-75 Hz, 30-95 kHz, 160 MHz max pixel clock).
$edid.AddRange([byte[]]@(0x00, 0x00, 0x00, 0xFD, 0x00, 48, 75, 30, 95, 16, 0x01,
                         0x0A, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20))

# Descriptor 3: monitor name.
$edid.AddRange([byte[]](New-TextDescriptor 0xFC $MonitorName))

# Descriptor 4: unused.
$edid.AddRange([byte[]]@(0x00, 0x00, 0x00, 0x10, 0x00,
                         0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00))

# Extension block count (byte 126)
$edid.Add([byte]0x00)

if ($edid.Count -ne 127) { throw "EDID body is $($edid.Count) bytes, expected 127 before checksum." }

$sum = 0
foreach ($b in $edid) { $sum += $b }
$checksum = [byte]((256 - ($sum % 256)) % 256)
$edid.Add($checksum)

$bytes = $edid.ToArray()

if ($Verify) {
    $total = 0
    foreach ($b in $bytes) { $total += $b }
    if ($bytes.Length -ne 128) { throw "EDID is $($bytes.Length) bytes, expected 128." }
    if (($total % 256) -ne 0) { throw "EDID checksum invalid (sum mod 256 = $($total % 256))." }
    Write-Host "EDID OK: 128 bytes, checksum 0x$('{0:X2}' -f $checksum), sum mod 256 = 0."
}

if ($EmitCpp -or -not $Verify) {
    $lines = for ($i = 0; $i -lt $bytes.Length; $i += 8) {
        $chunk = $bytes[$i..([math]::Min($i + 7, $bytes.Length - 1))]
        '    ' + (($chunk | ForEach-Object { '0x{0:X2}' -f $_ }) -join ', ') + ','
    }
    $lines | ForEach-Object { Write-Output $_ }
}
