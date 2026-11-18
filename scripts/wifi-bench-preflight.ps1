<#
.SYNOPSIS
  Read-only preflight for the PanelOS WiFi bench window, run on the host PC before the window.

.DESCRIPTION
  Nothing here changes the machine: no firewall rule, no network profile, no process, no file. It only reads
  and prints, so it can be rerun freely. It does NOT create the firewall rule (see docs/wifi-bench.md) and it
  never starts the bench listener.

  Checks, each printed as PASS / FAIL / INFO:
    1. SHA-256 of the executable (note it; compare after the window).
    2. The interface that owns -BindIp is physical, Up and ALREADY Private (PanelHost --wifi-bench-check, plus
       Get-NetConnectionProfile for the alias). The bench never changes a profile.
    3. No firewall rule already exists for the executable.
    4. No enabled inbound Allow rule without a program filter already opens -Port. Rules owned by an AppContainer
       (Owner set: Store apps, "ServerCapability" rules) and rules whose protocol is neither TCP nor Any (for
       example an ICMP rule) cannot open a TCP port for a classic exe and are ignored; see Test-BroadInboundRule.
    5. INFO: PanelHost processes that must be stopped (with approval) for the window.

  Exit code 0 only when every check passes.

  -SelfTest runs the rule-filter logic of check 4 over fixed examples (pure logic: it reads no firewall state and
  needs no executable) and exits 0 when they all behave as documented.
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Run')][string]$BindIp,
    [Parameter(Mandatory, ParameterSetName = 'Run')][string]$InterfaceAlias,
    [Parameter(Mandatory, ParameterSetName = 'SelfTest')][switch]$SelfTest,
    [int]$Port = 40002,
    [string]$Executable
)

# $PSScriptRoot is empty inside a param() default under some hosts, so the default is applied here.
if (-not $Executable) { $Executable = Join-Path $PSScriptRoot '..\windows\PanelHost\bin\Debug\net8.0-windows10.0.19041.0\PanelHost.exe' }

$ErrorActionPreference = 'Stop'
$failed = 0
function Report([string]$level, [string]$text) {
    Write-Host ("[{0}] {1}" -f $level, $text)
    if ($level -eq 'FAIL') { $script:failed++ }
}

# True when this inbound Allow rule would already open TCP $Port for any program. $Rule needs Name, Owner,
# Program, Protocol and LocalPort. An Owner means an AppContainer / per-package rule (Store apps), which
# does not apply to a classic exe; Protocol must be TCP (or its number 6) or Any, so an ICMP/UDP rule is not
# a TCP opening. A live example that must NOT match: "Radmin VPN icmpv4" (Protocol 1, restricted to 26.0.0.0/8).
function Test-BroadInboundRule($Rule, [int]$Port) {
    if ($Rule.Owner) { return $false }
    if ($Rule.Program -ne 'Any') { return $false }
    if (@('TCP', '6', 'Any') -notcontains "$($Rule.Protocol)") { return $false }
    return (@($Rule.LocalPort) -contains 'Any') -or (@($Rule.LocalPort) -contains "$Port")
}

if ($SelfTest) {
    $cases = @(
        @{ Name = 'Store ServerCapability, SYSTEM owner';   Rule = @{ Owner = 'S-1-5-18'; Program = 'Any'; Protocol = 'TCP'; LocalPort = 'Any' }; Expect = $false },
        @{ Name = 'Store ServerCapability, user SID owner'; Rule = @{ Owner = 'S-1-5-21-1-2-3-1001'; Program = 'Any'; Protocol = 'Any'; LocalPort = 'Any' }; Expect = $false },
        @{ Name = 'Radmin VPN icmpv4 (protocol 1)';         Rule = @{ Owner = $null; Program = 'Any'; Protocol = '1'; LocalPort = 'Any' }; Expect = $false },
        @{ Name = 'UDP rule on the port';                   Rule = @{ Owner = $null; Program = 'Any'; Protocol = 'UDP'; LocalPort = '40002' }; Expect = $false },
        @{ Name = 'Rule tied to another program';           Rule = @{ Owner = $null; Program = 'C:\x\y.exe'; Protocol = 'TCP'; LocalPort = 'Any' }; Expect = $false },
        @{ Name = 'TCP rule for another port';              Rule = @{ Owner = $null; Program = 'Any'; Protocol = 'TCP'; LocalPort = @('8080', '443') }; Expect = $false },
        @{ Name = 'Broad TCP, any port';                    Rule = @{ Owner = $null; Program = 'Any'; Protocol = 'TCP'; LocalPort = 'Any' }; Expect = $true },
        @{ Name = 'Broad TCP, this port among others';      Rule = @{ Owner = ''; Program = 'Any'; Protocol = '6'; LocalPort = @('80', '40002') }; Expect = $true },
        @{ Name = 'Broad any-protocol rule on this port';   Rule = @{ Owner = $null; Program = 'Any'; Protocol = 'Any'; LocalPort = '40002' }; Expect = $true }
    )
    foreach ($case in $cases) {
        $got = [bool](Test-BroadInboundRule ([pscustomobject]$case.Rule) $Port)
        if ($got -eq $case.Expect) { Report 'PASS' ("{0}: broad={1}" -f $case.Name, $got) }
        else { Report 'FAIL' ("{0}: expected broad={1}, got {2}" -f $case.Name, $case.Expect, $got) }
    }
    if ($failed -eq 0) { Write-Host 'SELFTEST PASS'; exit 0 }
    Write-Host "SELFTEST FAIL: $failed case(s)."; exit 1
}

$Executable = [System.IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $Executable)) {
    Report 'FAIL' "Executable not found: $Executable (build the solution first)."
    exit 1
}

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Report 'INFO' ("Elevated: {0}. PanelHost itself must never run elevated; only the firewall commands may." -f $elevated)

# 1) Hash before the window.
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Executable).Hash
Report 'INFO' "SHA-256 of the executable (write it down, compare after the window): $hash"

# 2) Interface: the host's own judgement, then the profile as Windows lists it for the alias.
& $Executable --wifi-bench-check --wifi-bind $BindIp | ForEach-Object { Write-Host "       $_" }
if ($LASTEXITCODE -eq 0) { Report 'PASS' 'PanelHost --wifi-bench-check: the bench would accept this address.' }
else { Report 'FAIL' "PanelHost --wifi-bench-check exit code ${LASTEXITCODE}: the bench would refuse this address." }
try {
    $profiles = @(Get-NetConnectionProfile -InterfaceAlias $InterfaceAlias)
    if ($profiles.Count -eq 1 -and "$($profiles[0].NetworkCategory)" -eq 'Private') {
        Report 'PASS' "Get-NetConnectionProfile: '$InterfaceAlias' is Private."
    } else {
        Report 'FAIL' ("Get-NetConnectionProfile: '{0}' is {1}. Do not change it for the bench; decide with the owner." -f $InterfaceAlias, (($profiles | ForEach-Object { $_.NetworkCategory }) -join ', '))
    }
} catch { Report 'FAIL' ("Cannot read the profile of '{0}': {1}" -f $InterfaceAlias, $_.Exception.Message) }

# 3) Existing rules for this executable (any profile, enabled or not).
try {
    $own = @(Get-NetFirewallApplicationFilter -Program $Executable -ErrorAction SilentlyContinue | Get-NetFirewallRule -ErrorAction SilentlyContinue)
    if ($own.Count -eq 0) { Report 'PASS' 'No firewall rule exists for this executable.' }
    else {
        Report 'FAIL' ("{0} firewall rule(s) already exist for this executable: {1}. Stop and decide before the window." -f $own.Count, (($own | ForEach-Object { $_.Name }) -join ', '))
    }
} catch { Report 'FAIL' "Cannot read firewall rules for the executable: $($_.Exception.Message)" }

# 4) Broad inbound Allow rules (no program filter) that would already open the port.
try {
    $broad = @(Get-NetFirewallRule -Direction Inbound -Action Allow -Enabled True | ForEach-Object {
        $app = $_ | Get-NetFirewallApplicationFilter; $portFilter = $_ | Get-NetFirewallPortFilter
        [pscustomobject]@{ Name = $_.Name; Owner = $_.Owner; Program = $app.Program; Protocol = $portFilter.Protocol; LocalPort = $portFilter.LocalPort }
    } | Where-Object { Test-BroadInboundRule $_ $Port })
    if ($broad.Count -eq 0) { Report 'PASS' "No broad inbound Allow rule already opens TCP $Port." }
    else { Report 'FAIL' ("{0} broad inbound rule(s) would already open TCP {1}: {2}" -f $broad.Count, $Port, (($broad | ForEach-Object { $_.Name }) -join ', ')) }
} catch { Report 'FAIL' "Cannot enumerate inbound rules: $($_.Exception.Message)" }

# 5) Running PanelHost processes (informational: stopping them is a separate, approved step).
$running = @(Get-Process -Name PanelHost -ErrorAction SilentlyContinue)
if ($running.Count -eq 0) { Report 'INFO' 'No PanelHost process is running.' }
else { Report 'INFO' ("PanelHost is running (PID {0}). It must be stopped, with approval, before the window and restarted afterwards." -f (($running | ForEach-Object { $_.Id }) -join ', ')) }

if ($failed -eq 0) { Write-Host 'PREFLIGHT PASS: nothing was changed.'; exit 0 }
Write-Host "PREFLIGHT FAIL: $failed check(s) failed. Nothing was changed."
exit 1
