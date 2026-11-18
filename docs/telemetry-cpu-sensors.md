# Temperatura e clock da CPU: diagnóstico

## Fallback de clock sem driver (09/11/2026)

Quando nenhum clock válido de núcleo é reportado pelo LibreHardwareMonitor,
o PanelHost usa os contadores locais Windows PDH de
`Processor Information(_Total)`: `Processor Frequency` (MHz nominais) ×
`% Processor Performance` / 100. É uma **estimativa agregada do Windows**,
não uma leitura instantânea por núcleo nem um clock base usado como valor atual.
O fallback só lê `_Total`: não fornece clocks individuais por núcleo/socket,
e não foi homologado em topologias com frequências nominais heterogêneas.
O contador de desempenho pode passar de 100 com boost; a leitura usa
`PDH_FMT_DOUBLE | PDH_FMT_NOCAP100`, sem limitar o boost a 100%.

`WindowsCpuClock` usa apenas `pdh.dll`, sem pacote ou driver adicional.
`PdhAddEnglishCounterW` aceita os nomes ingleses mesmo em Windows localizado.
A primeira coleta aquece o contador; a seguinte, no ciclo normal de 1 segundo,
calcula o valor. Não há espera bloqueante no laço. Falha de coleta, status do
contador inválido, ausência, zero, NaN/infinito ou resultado fora de (0, 10000]
MHz mantêm o clock indisponível; nenhum valor anterior ou frequência nominal
isolada é publicado. Um clock válido do LibreHardwareMonitor sempre tem prioridade.
O handle PDH é fechado no descarte do serviço.

O diagnóstico de saúde do clock contém `Source=WindowsProcessorPerformance`
ou `LibreHardwareMonitor`. O log registra mudanças de origem e o comando
`--telemetry-debug-cpu` mostra também o clock efetivamente publicado em GHz.
O protocolo v1 tem somente `cpu.clockGhz`, sem campo para origem ou voltagem;
foi preservado. A identificação da origem fica no modelo de saúde e nos logs.

Voltagem exige acesso PawnIO comprovado (`Loaded`). `Blocked`, `AccessDenied`,
`InstalledNotLoaded`, `NotInstalled` ou `Unknown` resultam em indisponível:
o valor bruto 1,55 V não é aceito. Os dumps de CPU e o probe identificam a rejeição
sem exibir o número como medição. Para temperatura e potência, a supressão ocorre
somente em `Blocked`, `AccessDenied`, `NotInstalled` e `InstalledNotLoaded`, que
comprovam falta de acesso. Em `Unknown`, o probe não permite concluir falha:
uma leitura válida da biblioteca é aceita; valores inválidos continuam nulos.
Essa decisão também vale para o diagnóstico/log de temperatura. A regra de
voltagem continua exigindo `Loaded`, inclusive quando o estado é `Unknown`.
Não há fallback para temperatura/potência e nenhuma consulta a zona térmica WMI.

O estado do driver é lido uma vez ao inicializar o serviço e não é reconsultado
na amostragem periódica nem nos dumps. Uma mudança de acesso ao driver exige
reiniciar o PanelHost para refazer esse diagnóstico; o fallback não implica
que o estado do PawnIO tenha mudado.

Fontes primárias da API:

- [Contadores Windows e Processor Information](https://learn.microsoft.com/en-us/windows/win32/perfctrs/about-performance-counters).
- [PdhAddEnglishCounterW: nomes independentes do idioma](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw).
- [PdhGetFormattedCounterValue: duas coletas e NOCAP100](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcountervalue).

Verificação local nesta data, Ryzen 5 5600G, com o probe isolado compilado desta
branch: PawnIO `Blocked`, problema 52; clock bruto dos núcleos zero/null;
fallback Windows aproximadamente 4,15 GHz; voltagem indisponível por acesso não
comprovado. Temperatura e potência continuam indisponíveis. O probe mantém o
exit 2, pois seu critério histórico exige temperatura **e clock bruto** válidos;
as linhas `CPU clock fallback` são a evidência separada da recuperação do clock.
Sem instalação, UAC ou mudança de HVCI/Secure Boot, e sem parar o host em uso.

Este documento descreve como o PanelHost obtém temperatura e clock da CPU, como
diagnosticar quando essas métricas somem, e o que já foi verificado nesta
máquina (AMD Ryzen 5 5600G).

## Como as métricas chegam ao PanelHost

`Load` da CPU vem de contadores do Windows e não precisa de acesso a anel 0.
`Tctl/Tdie`, os clocks P-state por núcleo, `Power` e `Voltage` vêm da SMU e de
MSRs do processador. O `LibreHardwareMonitorLib` 0.9.x alcança esses registradores
através do driver de kernel **PawnIO**, carregando nele os módulos embutidos
`RyzenSMU.bin` e `AMDFamily17.bin` e abrindo o objeto de dispositivo
`\\?\GLOBALROOT\Device\PawnIO`.

Consequência prática: quando o PawnIO não carrega, os sensores continuam
existindo — o `LibreHardwareMonitor` os cria na enumeração — mas reportam `0` ou
`null` para sempre. `Load` continua correto. Esse é exatamente o padrão de falha
que parece "regressão de sensores" e não é.

## Identidade da biblioteca

A versão de assembly não identifica o build: várias builds diferentes reportam
`0.9.6.0`. Use a versão informacional, que carrega o commit upstream.

| Campo | Valor nesta máquina |
| --- | --- |
| Origem | `PackageReference` NuGet, sem DLL local, sem fork |
| Versão fixada | `windows/Directory.Build.props`, `LibreHardwareMonitorLibVersion` |
| Versão de assembly | `0.9.6.0` |
| Versão informacional | `0.9.6+3d331e3370efb858411f19511373eff65a218701` |
| SHA-256 do arquivo | `C2274F91322207EE86C344C76E791E7FDF6507ACE95546ADE2C5DF3ABE274246` |

Os três últimos campos aparecem no cabeçalho de `--dump-sensors`,
`--telemetry-debug-cpu` e do probe.

## Ferramentas de diagnóstico

### `--telemetry-debug-cpu`

```powershell
dotnet run --project windows/PanelHost -- --telemetry-debug-cpu
```

Imprime a identidade da biblioteca, o estado real do driver de kernel e **todos**
os sensores da CPU — nome, tipo, identificador, valor, mínimo e máximo — depois de
cada um de três `hardware.Update()` consecutivos, separados por 250 ms. Os três
ciclos existem para separar um sensor que precisa de aquecimento de um que nunca
reporta; o laço de amostragem normal não espera e não foi alterado. É um snapshot
único: o modo não inicia a amostragem periódica.

### Probe isolado

```powershell
dotnet run --project windows/PanelHost.SensorProbe
```

Processo mínimo: `Computer { IsCpuEnabled = true }`, `Open()`, `Update()`, imprime
os sensores. Nenhum protocolo ou transporte roda nele. O fallback Windows é
mostrado separado dos sensores brutos. Se o probe também mostrar `Tctl/Tdie` e clocks zerados, o problema está no
`LibreHardwareMonitor`/PawnIO e não no `TelemetryService`.

O probe resolve a biblioteca pela mesma propriedade que o PanelHost, então os
dois carregam exatamente o mesmo binário. Para comparar builds:

```powershell
dotnet run --project windows/PanelHost.SensorProbe -p:LibreHardwareMonitorLibVersion=0.9.7-pre724
```

Códigos de saída: `0` temperatura e clock reportando, `2` não reportando, `1`
`Computer.Open()` falhou.

## Modelo de saúde

`unavailable` não distingue causas. Cada métrica de CPU é classificada em:

| Estado | Significado |
| --- | --- |
| `Available` | um sensor foi selecionado e reportou valor utilizável |
| `SensorNotPresent` | a biblioteca não expõe sensor desse tipo |
| `SensorPresentNoValue` | o sensor existe e não reportou nada neste ciclo |
| `HardwareAccessFailure` | o driver de kernel está inutilizável |
| `PermissionDenied` | o dispositivo existe e recusa este processo |
| `UnsupportedHardware` | acesso comprovadamente funcional **e** sensor ausente |

`UnsupportedHardware` nunca é inferido de leitura vazia. Só é concluído quando o
objeto de dispositivo do PawnIO foi realmente aberto e, ainda assim, o sensor não
existe.

O estado do driver vem de um probe funcional, não de existência de arquivo:

| Estado | Como é determinado |
| --- | --- |
| `Loaded` | `\\?\GLOBALROOT\Device\PawnIO` abriu |
| `AccessDenied` | abertura recusada com `ERROR_ACCESS_DENIED` |
| `Blocked` | nó PnP do dispositivo reporta código de problema |
| `InstalledNotLoaded` | pacote registrado, dispositivo inexistente |
| `NotInstalled` | sem serviço e sem diretório de instalação |
| `Unknown` | o probe falhou; nada pode ser concluído |

## Caso observado em 2026-08-19 (Ryzen 5 5600G)

Sintoma: `CPU Temperature` e `CPU Clock` sem valor, `CPU Load` normal, host
elevado.

Causa raiz: **o pacote PawnIO instalado nesta máquina não tem assinatura válida
para modo kernel**, então o Windows nunca carrega o driver e o nó
`ROOT\PAWNIO\0000` fica em `CM_PROB_UNSIGNED_DRIVER` (código 52).

Prova, em três fontes independentes:

- `signtool verify /kp PawnIO.sys` → `Signing Cert does not chain to a Microsoft
  Root Cert`. `signtool verify /pa /all` mostra **uma única** assinatura
  (namazso.eu / GLOBALTRUST 2015), sem assinatura aninhada da Microsoft.
- O catálogo do pacote (`PawnIO.cat` no DriverStore e `oem71.cat` em `CatRoot`,
  2310 bytes) não tem assinatura nenhuma.
- `setupapi.dev.log`, instalação original de 2026-08-11 05:15:
  `Verifying file against specific (valid) catalog failed` com
  `exit(0x800b0100)` (`TRUST_E_NOSIGNATURE`), `Signer Score - Not digitally
  signed` e, por fim, `Driver package does not contain a signature, but user
  wants to install anyway` / `Signer Score = 0x80000000 (Unsigned)`. O pacote
  entrou porque o prompt de driver não assinado foi aceito.

Com Secure Boot e HVCI ativos — e HVCI está ativo desde antes do período
investigado (`Kernel-Boot` 153: `VBS Enabled, VSM Required, Hvci`) — um pacote
nesse estado não carrega em nenhum boot. Os eventos
`Microsoft-Windows-CodeIntegrity/Operational` para `oem71.cat`
(`Status 0xC0000428`, `STATUS_INVALID_IMAGE_HASH`) e para `PawnIO.sys`
(`file hash could not be found on the system`) aparecem a partir do boot de
2026-08-16; isso marca quando o Windows passou a registrar a rejeição, e não o
momento em que o driver teria deixado de funcionar.

Reinstalar não resolve. Em 2026-08-19 03:51 o `PawnIO_setup.exe` rodou depois do
`uninstall.exe` e o log registra `Driver package '...pawnio.inf_amd64_a72a2f969b8b7496\pawnio.inf' is already imported`
— o pacote antigo foi reaproveitado, e o probe elevado logo depois continuou em
`state: Blocked`, código 52.

Também não é cópia corrompida: o `PawnIO_setup.exe` local tem 3410960 bytes e
SHA-256 `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032`,
idênticos ao asset do release oficial 2.2.0 (publicado em 2026-03-15), e a
assinatura do próprio executável é válida. O pacote sem assinatura veio assim do
release; baixar de novo não muda nada.

A máquina confia normalmente na cadeia de atestação da Microsoft — `amdafd.cat` e
os demais catálogos do DriverStore validam como
`CN=Microsoft Windows Hardware Compatibility Publisher`. O problema é específico
do catálogo do PawnIO.

Correção: instalar uma build do PawnIO cujo **catálogo** tenha assinatura válida.
O critério de aceitação, antes de instalar qualquer coisa, é

```powershell
Get-AuthenticodeSignature <caminho>\PawnIO.cat | Format-List Status,SignerCertificate
```

terminar em `Valid` com um signatário da Microsoft. Enquanto isso não acontecer,
o Windows recusa a carga com Secure Boot e HVCI ativos.

Não voltar para o WinRing0: ele é o driver legado inseguro que o PawnIO
substituiu. Também não desativar integridade de memória nem Secure Boot para
contornar: isso rebaixa a segurança da máquina inteira para recuperar duas
métricas de telemetria. Nenhuma mudança na biblioteca recupera os sensores
enquanto o acesso a anel 0 não existir.

Não é regressão do PanelOS: nenhum código de telemetria mudou, e a versão do
`LibreHardwareMonitorLib` está em `0.9.6` desde 2026-08-07.

Não é regressão do `LibreHardwareMonitorLib`: o probe isolado dá o mesmo
resultado com `0.9.6+3d331e3` e com `0.9.7-pre724+825dc3de`.

Verificação depois de instalar uma build assinada:

```powershell
dotnet run --project windows/PanelHost.SensorProbe
```

`state: Loaded` no bloco do driver e valores diferentes de zero em
`Core (Tctl/Tdie)` e nos clocks por núcleo confirmam a recuperação física. Só
então o log do PanelHost volta a mostrar `CPU Temperature: Core (Tctl/Tdie)` e
`CPU Clock: aggregate sensor 'Cores (Average)'`.

## O que os testes cobrem — e o que não cobrem

`windows/PanelHost.Tests` cobre sensor ausente, presente sem valor, presente com
valor, múltiplos clocks, clocks parcialmente e totalmente nulos, a chamada de
`hardware.Update()` (inclusive em sub-hardware) e cada estado de saúde.

Testes de unidade **não** provam que o PawnIO voltou a funcionar. Eles provam que
o caminho de código está correto. A validação física exige o probe em uma máquina
com o driver carregado.
