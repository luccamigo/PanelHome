> **Nota (publicação):** os arquivos brutos (`raw/`: JSON/CSV/logs de execução) não fazem parte deste repositório público, pois continham caminhos e dados da máquina de teste. Os números agregados estão em `comparison.md`; referências a `raw/` neste diretório descrevem o histórico original.

# Transport benchmark

This directory preserves the reproducible PanelOS comparison between the
development ADB/TCP carrier and the native USB carrier. The benchmark started
when ADB was the baseline and native USB was experimental; today native USB is
preferred and ADB/TCP is the supported fallback. See
[`../../transport-architecture.md`](../../transport-architecture.md) for the
current product behavior. Dated results below remain historical evidence.

## Current status (2026-08-12)

The Windows development package was built with Inf2Cat, test-signed, installed,
and physically verified on the test tablet. The exact binding is now:

```text
USB\VID_18D1&PID_2D01&MI_00 -> oem73.inf / PanelOS / WINUSB
USB\VID_18D1&PID_2D01&MI_01 -> oem69.inf / Samsung / WINUSB (ADB)
```

Physical discovery was exercised on the available test tablet:

1. The host opened the OEM ADB WinUSB interface (`FF/42/01`).
2. AOA requests 51, 52, and 53 succeeded.
3. The tablet re-enumerated as AOA+ADB (`VID_18D1/PID_2D01`).
4. Android reported `current_functions=ACCESSORY` and the PanelOS identity.
5. Before installation, Windows bound `MI_00` to `wpdmtp.inf/WUDFWpdMtp`.
6. After installation, automatic GUID discovery opened WinUSB and found bulk IN
   `0x81`, bulk OUT `0x01`, max packet 512 bytes.
7. PLMX, Dashboard/telemetry, Stream Deck, Control, and H.264 decode completed.
8. Repeated normal USB -> AOA transitions rebound MI_00 automatically.

The fixed Dashboard benchmark completed for both carriers with 2,000 retained
RTT samples at 32/128/512/1024 B, three repetitions, and bidirectional
1/8/32 MiB bulk transfers. Raw JSON/CSV are retained in `raw/`. No value is
estimated. Only the evidence explicitly promoted under the retention policy is
versioned; new runs are local or CI artifacts by default.

The loaded H.264 benchmark is not complete. The installed display driver rejects
an unelevated PanelHost with Win32 error 5, and the UAC prompt for an isolated
elevated benchmark host was not available in this session. The H.264 physical
smoke used a deterministic development source and proved transport/decode, but
is not substituted for the required real 1280x800 loaded comparison.

## Files

- `methodology.md`: fixed workload, commands, warm-up, repetition, metrics, and
  interpretation rules.
- `comparison.md`: required result table and current evidence-based conclusion.
- `retention-policy.md`: classification and promotion rules for benchmark artifacts.
- `schema.md`: JSON/CSV output fields.
- `raw/physical-usb-probe-20260812.txt`: sanitized pre-binding probe evidence.
- `raw/physical-winusb-validation-20260812.md`: sanitized binding and physical
  functional evidence.
- `raw/20260812-004953-adb_tcp-dashboard.{json,csv}` and
  `raw/20260812-010756-usb_native_experimental-dashboard.{json,csv}`: valid
  fixed-workload Dashboard benchmark artifacts.

## Quick commands

Probe only (this intentionally transitions the connected Android device to AOA):

```powershell
dotnet run --project windows\PanelHost\PanelHost.csproj -- --transport usb --usb-probe
```

Run a full ADB benchmark after stopping any other PanelHost supervisor:

```powershell
.\scripts\run-transport-benchmark.ps1 -Transport adb -Scenario dashboard
.\scripts\run-transport-benchmark.ps1 -Transport adb -Scenario desktop-static
.\scripts\run-transport-benchmark.ps1 -Transport adb -Scenario desktop-dynamic
```

Repeat with `-Transport usb`. The script preflights PanelHost/Control Center and
ports 8765-8767, never kills an existing host, records the PID it creates, and
always selects H.264, 60 FPS preferred, and the existing 10 Mbps encoder
configuration.

By default these commands write to `.artifacts/benchmarks/transport/`, which is
ignored by Git. Use `-OutputDirectory` for a CI artifact staging directory. A
curated result is copied into `raw/` only when it satisfies the retention policy.
