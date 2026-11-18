# Live session and Android smoke tests

## Existing coverage gap

`protocol-fixtures/` proves deterministic JSON bytes in C# and Kotlin, but it does not
exercise connection lifetime, handshake negotiation, state delivery, disconnect, or
reconnect across both runtimes. The Android instrumented source set also previously had
no Activity/Service smoke coverage.

## Cross-language harness

On Windows, run:

```powershell
./scripts/run-cross-language-live-session.ps1
```

The script assembles the Kotlin unit-test client before starting the live host, so cold
compilation does not consume its three-minute lifetime. It then starts the MSTest host
in `CrossLanguageLiveSessionHostTests`, waits for its
ephemeral loopback ports, and runs only `CrossLanguageLiveSessionTest` through Gradle.
Only the test task is forced to rerun (`--rerun`); compiled dependencies remain up to date.
No fixed port, device, ADB session, USB accessory, display driver, or GPU is required.

Successful endpoints execute the production C# `PanelClientSession` and production
Kotlin `TransportPanelConnection`. The Kotlin-only loopback carrier supplies raw TCP;
all JSONL encoding, bounded-line reading, feature negotiation, and event projection use
production code. Fake C# telemetry/media/volume/desktop services provide deterministic
state. The Stream Deck endpoint uses the same shared runtime used by existing host tests.

Covered scenarios are HELLO/HELLO_ACK, telemetry, media and volume state, desktop state,
Stream Deck layout, clean disconnect, wrong protocol version, missing Stream Deck
feature, invalid JSONL, handshake drop, reconnect, and stale Stream Deck revision.
The invalid/drop endpoints are deliberate stream fault injectors, not alternate protocol
implementations.

## Android instrumented smoke

Run on an emulator or connected device:

```powershell
./gradlew.bat connectedDebugAndroidTest
```

`MainActivitySmokeTest` covers Activity creation, foreground Service bind, Activity
recreation/rebind, UI receipt of Service-owned session state, and Desktop-mode state
projection. A process-local fake `PanelSession` is injected into the real Service binder;
no physical transport is opened.

## Build identity diagnostics

Build both artifacts and inspect their embedded metadata:

```powershell
./gradlew.bat assembleDebug
dotnet build windows/PanelHost/PanelHost.csproj
./scripts/verify-build-identity.ps1 -IntendedApkBuildIdentity 'PanelHome/1.0+1'
```

The command prints host/APK identities, build numbers, and protocol versions. It warns
when the host build number is newer, when protocol versions differ, or when an explicitly
intended APK identity does not match. Timestamps and Git SHAs are never compatibility
requirements; protocol versions remain the contract.

## Limitations

The loopback harness does not exercise AOA/WinUSB, physical reconnect behavior,
PanelDisplayDriver, video encoding/decoding, or GPU rendering. Instrumented tests require
an Android emulator; the dedicated CI job is separate so failures are attributable to
emulator infrastructure rather than host/protocol tests.
