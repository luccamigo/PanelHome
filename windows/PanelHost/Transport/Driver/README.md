# PanelOS AOA WinUSB binding

This INF binds only the AOA+ADB data interface:

- `USB\VID_18D1&PID_2D01&MI_00`

It deliberately does not match the ADB interface `FF/42/01`. The PanelHost
discovery GUID is `{4F9B4A5C-7A77-4B21-9E4A-5C4C54D98E9D}`.

The development workflow is scripted from the repository root:

```powershell
.\scripts\build-aoa-winusb-driver.ps1 -CertificateThumbprint '<explicit thumbprint>'
# Put the tablet into AOA mode, then open an elevated PowerShell:
.\scripts\install-aoa-winusb-driver.ps1
.\scripts\verify-aoa-winusb-driver.ps1
# Development rollback (elevated):
.\scripts\uninstall-aoa-winusb-driver.ps1
```

Build validates the exact model, runs WDK `Inf2Cat`, optionally signs the CAT
only when a certificate thumbprint is explicitly supplied, and writes the
package under `.artifacts/`. No private key, PFX, certificate, or machine path is
stored in the repository. Install and uninstall require elevation. None of the
scripts changes BCD, Secure Boot, Test Signing, or certificate stores.

On the physically tested Samsung stack, Windows initially selected
`wpdmtp.inf`/`WUDFWpdMtp` for `MI_00` even though its descriptor is `FF/FF/00`.
The development package was test-signed and installed as `oem73.inf` on
2026-08-12. Verification showed `MI_00 -> WINUSB` and the separate
`MI_01 -> oem69.inf/Samsung WINUSB` ADB binding unchanged. Production still
requires the normal Microsoft/organization release-signing path; this test CAT
is not a public release signature.
