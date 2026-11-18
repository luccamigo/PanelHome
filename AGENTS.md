# Repository Guidelines

## Project Structure

PanelOS is a multi-runtime repository:

- `app/` contains the single-module Android application, PanelHome. Kotlin code
  lives under `app/src/main/java/com/panelos/panelhome/`; resources and the
  manifest live under `app/src/main/res/` and `app/src/main/AndroidManifest.xml`.
- `windows/` contains the .NET 8 host, Control Center, display broker, shared
  libraries, and their tests. Every `.csproj` under this directory must be
  included in `windows/PanelOS.sln`.
- `driver/PanelDisplayDriver/` contains the C++ indirect display driver, its
  command-line tool, tests, packaging scripts, and driver-specific README.
- `protocol-fixtures/` is the shared protocol corpus used by Kotlin and C# tests.
- `scripts/` contains development, validation, benchmark, and driver helpers.
- `docs/` contains architecture, operations, protocol, and benchmark documents;
  begin with `docs/README.md`.

Keep dependency versions in `gradle/libs.versions.toml` for Android and
`windows/Directory.Build.props` when a package version must remain aligned
across Windows projects.

## Build and Test Commands

Run commands from the repository root. On macOS or Linux, replace
`.\gradlew.bat` with `./gradlew`.

```powershell
.\gradlew.bat testDebugUnitTest
.\gradlew.bat lintDebug
.\gradlew.bat assembleDebug
.\gradlew.bat connectedDebugAndroidTest

.\scripts\verify-solution-complete.ps1
dotnet build windows/PanelOS.sln
dotnet test windows/PanelOS.sln --no-build
```

`connectedDebugAndroidTest` requires an emulator or device. The C++ driver has a
separate WDK toolchain and is intentionally outside `windows/PanelOS.sln`; use
the commands in `driver/PanelDisplayDriver/README.md` when changing it.

For a physical Android development session with ADB reverse and PanelHost, use
`.\scripts\start-panel-dev.ps1` after reviewing its prerequisites in the root
README.

## Coding Style and Architecture

Use four-space indentation and same-line opening braces across Kotlin, C#,
Kotlin DSL, and XML. Follow the existing language conventions: `PascalCase` for
types and composables, `camelCase` for Kotlin members, standard .NET naming for
C#, and `lowercase_snake_case` for Android resources.

Keep Android composables stateless where practical and route state through the
presentation layer and `StateFlow`. Preserve the existing Windows separation
between host/runtime code, shared OS integrations, protocol contracts, and UI.
Protocol changes must remain compatible across Android and Windows and should
update the shared fixtures when serialization changes.

Use Android Studio's and Visual Studio's default formatters; no repository-wide
formatter is configured.

## Testing Guidelines

Android local tests belong in `app/src/test/java/`; device tests belong in
`app/src/androidTest/java/`. Windows tests belong in the matching `*.Tests`
project. Name test files after the subject under test and describe observable
behavior in test methods.

Prefer host-side tests for state transitions, serialization, and business
logic. Add instrumented tests only for Android framework or UI behavior, and
driver tests only when the native contract is involved. For protocol changes,
run both Kotlin and .NET suites because both consume `protocol-fixtures/`.

## Documentation and Generated Files

Update the nearest README or document whenever commands, defaults, protocols,
or operational behavior change. Treat dated benchmark reports as historical
evidence; the current supported behavior belongs in the root README and the
architecture documents. Follow
`docs/benchmarks/transport/retention-policy.md` before committing benchmark
output.

Do not commit IDE metadata, `local.properties`, SDK paths, signing material,
build output, logs, dumps, or transient benchmark results. The root `.gitignore`
is the source of truth for repository-wide exclusions; module-specific ignore
files may add rules needed when a module is opened independently.

## Commits and Pull Requests

Use short, imperative commit subjects and keep each commit focused. Pull
requests should explain scope and rationale, link relevant issues, list the
commands run and their results, and include screenshots for visible UI changes.
Run the relevant unit tests and lint before requesting review.
