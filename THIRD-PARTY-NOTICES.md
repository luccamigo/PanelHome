# Avisos de terceiros

Lista das dependências declaradas no código, conferidas nos arquivos de projeto
(`windows/**/*.csproj`, `windows/Directory.Build.props`, `gradle/libs.versions.toml`,
`app/build.gradle.kts`). A coluna "Situação" diz o que foi verificado. As licenças
marcadas como "Não conferido" **não foram checadas no pacote baixado**.

## Windows (.NET)

| Pacote | Versão declarada | Licença | Situação |
|---|---|---|---|
| LibreHardwareMonitorLib | 0.9.6 (fixada em `Directory.Build.props`) | MPL-2.0 (indicada, não conferida no pacote) | Usada sem modificação (referência NuGet, sem cópia de código no repositório). O ZIP do Windows redistribui a DLL, por isso o pacote deve manter o aviso de copyright do upstream e oferecer o código-fonte correspondente: <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>. O copyright exato do upstream não foi conferido no pacote. |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.8 | MIT (nuspec local) | Conferido no nuspec do cache NuGet; copyright Microsoft |
| Vortice.MediaFoundation | 3.8.3 | MIT (nuspec local) | Conferido; copyright Amer Koleci and Contributors |
| System.CommandLine | 2.0.0 | MIT (nuspec local) | Conferido; copyright Microsoft |
| System.Text.Json | 9.0.1 | MIT (nuspec local) | Conferido; copyright Microsoft |
| Microsoft.Extensions.Hosting | 8.0.1 | MIT (nuspec local) | Conferido; copyright Microsoft |
| QRCoder | 1.6.0 | MIT (nuspec local) | Conferido |
| Microsoft.NET.Test.Sdk, MSTest.TestAdapter, MSTest.TestFramework | 17.11.1; 3.6.4 | Não conferido (apenas testes) | Não está no cache local; verificar |

HidSharp não aparece nas referências de código do snapshot; não foi listado.

## PawnIO

- Não é distribuído neste repositório nem no pacote público.
- Código e instalador: <https://github.com/namazso/PawnIO> e
  <https://github.com/namazso/PawnIO.Setup>.
- O documento interno `docs/pawnio-redistribution.md` registra GPL-2.0-or-later, com
  exceção para software LGPL e independentes. Antes de qualquer redistribuição do
  instalador, conferir a licença e os termos próprios.

## Android (Gradle)

Versões em `gradle/libs.versions.toml`. Licenças não conferidas.

| Pacote | Versão |
|---|---|
| Conscrypt (org.conscrypt) | 2.9.0 |
| ZXing core (com.google.zxing) | 3.5.4 |
| org.json (json) | 20260719 |
| JUnit | 4.13.2 |
| Robolectric | 4.17 |
| Roborazzi | 1.76.0 |
| AndroidX (Activity, Lifecycle, Compose, CameraX, Test) | ver `libs.versions.toml` |
| Kotlin | 2.3.21 |
