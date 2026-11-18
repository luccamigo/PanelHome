# PanelDisplayDriver

Indirect Display Driver (IddCx / UMDF 2) que cria e remove sob comando um monitor
virtual **PanelOS** no Windows.

Esta etapa preserva o ciclo de vida já validado e acrescenta a captura
latest-only necessária para o milestone First Pixels:

```text
Estado inicial          Windows enxerga apenas os monitores físicos
       |
   CONNECT              PanelDisplayDriver anuncia o monitor virtual
       |
       v                Windows passa a enxergar um monitor adicional
       |
   DISCONNECT           PanelDisplayDriver sinaliza a saída do monitor
       |
       v                Windows volta ao estado sem esse monitor
```

O driver publica apenas o snapshot CPU mais recente da swapchain para o
PanelHost por IOCTL. Encoder, USB, socket e código Android continuam fora deste
componente. Veja [Limitações](#limitações).

---

## Arquitetura

Posição no produto final:

```text
PanelHome (Android)
      | USB
PanelHost (Windows)
      | IOCTL  <-- interface implementada aqui
PanelDisplayDriver
      |
Windows Virtual Monitor
```

Dentro do driver:

```text
Driver.cpp            DriverEntry, DeviceAdd, D0Entry/D0Exit, registro dos callbacks IddCx
  |
Device.cpp            adapter IddCx + máquina de estados + tratamento dos IOCTLs
  |                   (é aqui que CONNECT/DISCONNECT viram trabalho de IddCx)
  +-- Monitor.cpp     IDDCX_MONITOR, reporte de modos, arrival/departure
        |
        +-- SwapChainProcessor.cpp   thread do swapchain + staging capture a 10 Hz
```

Lógica pura, sem WDF/IddCx, compartilhada e testável:

| Arquivo | Responsabilidade |
| --- | --- |
| `driver/Public.h` | Contrato público: GUID da interface, IOCTLs, estruturas |
| `common/PanelMonitorState.*` | Máquina de estados connect/disconnect e idempotência |
| `common/PanelDisplayModes.*` | Lista de modos e validação de modo |
| `common/LatestFrameStore.*` | Snapshot latest-only, geração e sequência de frame |

A separação existe por um motivo prático: `PanelMonitorState` decide **o que deve
acontecer** e `Device.cpp` executa as chamadas IddCx correspondentes. É isso que
permite testar as regras (incluindo chamadas duplicadas e o limite de 0 ou 1
monitor) sem driver instalado.

### Estrutura de diretórios

```text
driver/PanelDisplayDriver/
├── PanelDisplayDriver.sln
├── driver/                       projeto do driver (requer WDK)
│   ├── Driver.cpp/.h
│   ├── Device.cpp/.h
│   ├── Monitor.cpp/.h
│   ├── SwapChainProcessor.cpp/.h
│   ├── Trace.cpp/.h              TraceLogging
│   ├── Edid.h                    bloco EDID gerado
│   ├── Public.h                  contrato compartilhado
│   └── PanelDisplayDriver.inf
├── common/                       lógica pura compartilhada
├── tools/
│   ├── PanelDisplayCtl/          ferramenta de linha de comando
│   └── Generate-Edid.ps1         gerador/validador do EDID
├── tests/                        testes unitários
└── scripts/                      build, package, install, uninstall
```

---

## Como o IddCx é usado

### Adapter

Criado em `D0Entry` via `IddCxAdapterInitAsync`, com
`IDDCX_ADAPTER_CAPS.MaxMonitorsSupported = 1`. Quando
`EvtIddCxAdapterInitFinished` chega com sucesso, o driver passa ao estado
`Ready`.

**Nenhum monitor é criado nesse caminho.** Esta é a diferença deliberada em
relação ao `IddSample` oficial da Microsoft, que cria os monitores em `D0Entry`.
Aqui o driver fica instalado e carregado com zero monitores até receber
`IOCTL_PANEL_CONNECT_DISPLAY`.

### Monitor

No CONNECT:

1. a máquina de estados valida que ainda não existe monitor;
2. `IddCxMonitorCreate` com `IDDCX_MONITOR_DESCRIPTION_TYPE_EDID` (bloco de
   `Edid.h`) e o `MonitorContainerId` estável do único conector PanelOS;
3. `IddCxMonitorArrival` anuncia a chegada;
4. Windows consulta os modos e ativa o monitor.

No DISCONNECT, o driver encerra e destrói a swapchain e só então chama
`IddCxMonitorDeparture`. Essa chamada destrói o `IDDCX_MONITOR`; o driver não
chama `WdfObjectDelete` nesse handle e não o reutiliza. Um reconnect cria um
novo objeto de monitor com a mesma identidade estável.

Os modos são reportados em três callbacks:

| Callback | Fonte da resposta |
| --- | --- |
| `EvtIddCxParseMonitorDescription` | lista padrão (é global, não recebe objeto) |
| `EvtIddCxMonitorGetDefaultDescriptionModes` | lista padrão (rede de segurança) |
| `EvtIddCxMonitorQueryTargetModes` | `ModeTable` do próprio monitor |

`EvtIddCxAdapterCommitModes` registra o modo que o Windows ativou, para que
`status` possa reportá-lo.

### Swapchain

`EvtIddCxMonitorAssignSwapChain` cria um `Direct3DDevice` no adaptador de
renderização indicado e inicia uma thread dedicada (prioridade MMCSS
`Distribution`, como no sample oficial). O laço faz
`IddCxSwapChainReleaseAndAcquireBuffer` → conta/captura quando devido →
`IddCxSwapChainFinishedProcessingFrame`.

`ProcessFrame()` limita a captura ao target configurado (30/45/60 FPS) com
deadlines acumulativos e reutiliza uma texture D3D11 staging. Frames atrasados
avançam o deadline sem burst de recuperação: o snapshot mais recente vence. O
Host altera o target por `IOCTL_PANEL_SET_CAPTURE_CONFIG`, sem recriar monitor
ou swapchain.
O `Map(D3D11_MAP_FLAG_DO_NOT_WAIT)` nunca espera GPU; se o recurso ainda estiver
ocupado, aquele snapshot é descartado. A publicação troca apenas um
`shared_ptr` sob SRW lock curto: não existe fila de frames e o swapchain nunca
espera PanelHost, USB, compressão ou Android.

Cada assign inicia uma geração nova. Unassign, departure, disconnect e device
stop invalidam a geração anterior, impedindo pixels de uma sessão antiga de
reaparecerem. Não há log por frame.

### Locking

`Device.cpp` usa um único `WDFWAITLOCK`, e ele **não** é mantido durante as
chamadas IddCx (`IddCxMonitorArrival` / `IddCxMonitorDeparture`), porque essas
chamadas podem reentrar no driver de forma síncrona e causar deadlock. O que
garante a segurança é o flag de transição da máquina de estados: depois que
`BeginConnect`/`BeginDisconnect` retorna `true`, nenhum outro comando inicia
trabalho, e os ponteiros do monitor são limpos **antes** da destruição do objeto.

---

## Controle a partir do user-mode

O canal é `EvtIddCxDeviceIoControl`, que é o mecanismo documentado pela Microsoft
para "comunicação entre um processo de sessão de usuário e o driver [...] usado
por uma aplicação companheira para controlar algum aspecto do driver".

Interface: `GUID_DEVINTERFACE_PANEL_DISPLAY`
(`{2F21C12F-51EC-48CB-93D1-776DC7468B1B}`), publicada com
`WdfDeviceCreateDeviceInterface`.

| IOCTL | Entrada | Saída | Access |
| --- | --- | --- | --- |
| `IOCTL_PANEL_CONNECT_DISPLAY` | `PANEL_CONNECT_INPUT` (opcional) | `PANEL_COMMAND_OUTPUT` | `FILE_WRITE_ACCESS` |
| `IOCTL_PANEL_DISCONNECT_DISPLAY` | — | `PANEL_COMMAND_OUTPUT` | `FILE_WRITE_ACCESS` |
| `IOCTL_PANEL_GET_STATUS` | — | `PANEL_STATUS_OUTPUT` | `FILE_READ_ACCESS` |
| `IOCTL_PANEL_GET_FRAME_INFO` | `PANEL_FRAME_QUERY_INPUT` | `PANEL_FRAME_OUTPUT` | `FILE_READ_ACCESS` |
| `IOCTL_PANEL_READ_LATEST_FRAME` | `PANEL_FRAME_READ_INPUT` | `PANEL_FRAME_OUTPUT` + pixels | `FILE_READ_ACCESS` |

O protocolo do driver é v3. `GET_FRAME_INFO` usa `METHOD_BUFFERED` somente para
negociar geração, sequência e tamanho. `READ_LATEST_FRAME` usa
`METHOD_OUT_DIRECT`, para que WDF mapeie as páginas de saída do caller em vez de
copiar megabytes pelo system buffer. Se não houver novidade, retorna
`NoFrame`/`NoNewFrame`; se o snapshot mudar entre as chamadas, retorna
`FrameReplaced` sem payload e o caller repete a negociação.

### Segurança do dispositivo

O campo *access* do `CTL_CODE` é verificado pelo I/O manager contra o acesso que
o handle realmente recebeu, então é uma fronteira de privilégio de verdade, não
documentação. `GET_STATUS` só lê, logo pede `FILE_READ_ACCESS`; `CONNECT` e
`DISCONNECT` mudam o que o Windows enxerga, logo pedem `FILE_WRITE_ACCESS` e não
podem ser emitidos por um handle somente-leitura. Nenhum é `FILE_ANY_ACCESS`.

O `PanelDisplayCtl` abre o dispositivo com exatamente o necessário: `status`
usa `GENERIC_READ`, `connect`/`disconnect` usam `GENERIC_WRITE`.

Quem pode abrir a interface é definido pelo descritor de segurança no INF
(`[PanelDisplay_HardwareDeviceSettings]`):

```text
D:P(A;;GA;;;SY)(A;;GA;;;BA)
```

Isto é o `SDDL_DEVOBJ_SYS_ALL_ADM_ALL` de `wdmsec.h`:

| Elemento | Significado |
| --- | --- |
| `D:` | inicia a DACL |
| `P` | *protected* — não herda ACEs do pai, para que um pai permissivo não amplie o acesso |
| `(A;;GA;;;SY)` | permite `GENERIC_ALL` a `NT AUTHORITY\SYSTEM`; necessário porque o PnP manager e o host UMDF operam como LocalSystem |
| `(A;;GA;;;BA)` | permite `GENERIC_ALL` a `BUILTIN\Administrators`; é o que permite o `PanelDisplayCtl` elevado abrir a interface |

Não há ACE para `WD` (Everyone) nem `IU` (Interactive Users) — processos não
elevados não conseguem criar nem destruir monitores. `DeviceCharacteristics` é
`FILE_DEVICE_SECURE_OPEN` (0x100), aplicando a mesma ACL a nomes dentro do
namespace do dispositivo.

Estados e idempotência:

```text
DriverReady          adapter inicializado; connect permitido
MonitorDisconnected  nenhum monitor
MonitorConnected     exatamente um monitor

CONNECT    com monitor presente  -> PanelCommandResultAlreadyConnected
DISCONNECT sem monitor           -> PanelCommandResultAlreadyDisconnected
CONNECT    antes do adapter      -> PanelCommandResultAdapterNotReady
```

Nenhum desses casos é erro nem gera estado inconsistente.

---

## Requisitos

| Item | Versão |
| --- | --- |
| Windows | 11 (build 22000 ou superior) |
| Visual Studio | 2022 (v143) ou 2026 (v145) |
| Workload | Desenvolvimento para desktop com C++ |
| Windows SDK | 10.0.28000.0 |
| **WDK** | **mesma versão do SDK — obrigatório para o driver** |
| Target Platform | Universal, x64 |
| UMDF | 2 |
| IddCx | 1.2 (`UmdfExtensions=IddCx0102`) |

### Ambiente efetivamente utilizado

| Item | Valor |
| --- | --- |
| Visual Studio | Community 2026, `C:\Program Files\Microsoft Visual Studio\18\Community` |
| Toolset (user-mode) | v145 |
| Toolset (driver) | `WindowsUserModeDriver10.0` |
| MSVC | 14.51.36231 |
| Windows SDK / WDK | 10.0.28000.0 (`NTDDI_VERSION=0xA000012`) |
| UMDF | 2.33 (`WdfDriverStubUm.lib`) |
| IddCx | 1.2 (`Lib\10.0.28000.0\um\x64\iddcx\1.2\IddCxStub.lib`) |
| Plataforma | x64, Universal |

Resultado: os três projetos compilam em Debug x64 com **0 erros e 0 warnings**,
incluindo a análise estática de driver (PREfast / `DriverMinimumRules.ruleset`).

#### Duas instalações do Visual Studio

Esta máquina tem duas: **Build Tools 2026** e **Community 2026**. O WDK integrou
apenas na Community, então `scripts/build.ps1` escolhe a instalação que
realmente possui o toolset `WindowsUserModeDriver10.0` em vez de simplesmente
usar a mais recente.

#### É obrigatório usar o MSBuild de 64 bits

A task de verificação de INF do WDK carrega uma DLL nativa cuja arquitetura
acompanha a do processo do MSBuild, e o WDK 10.0.28000 só distribui as versões
x64 e arm64. Com o MSBuild de 32 bits (`MSBuild\Current\Bin\MSBuild.exe`) o
build emite:

```text
error : INF verification exception: Não é possível carregar a DLL 'x86\InfVerif.dll'
```

O detalhe perigoso é que **o MSBuild continua e termina com exit code 0**: o
pacote sai gerado, porém sem ter passado pela verificação de INF. Por isso
`scripts/build.ps1` usa `MSBuild\Current\Bin\amd64\MSBuild.exe`. Se for compilar
à mão, use o mesmo caminho.

### Sobre a versão do IddCx

O driver usa apenas APIs presentes no IddCx 1.2 (`IddCxAdapterInitAsync`,
`IddCxMonitorCreate`, `IddCxMonitorArrival`, `IddCxMonitorDeparture` e as
funções de swapchain). A versão está fixada em três lugares que precisam mudar
juntos:

- `driver/PanelDisplayDriver.vcxproj`: `IDDCX_VERSION_MAJOR/MINOR`
- as opções `/DIDDCX_VERSION_*` no mesmo arquivo
- `driver/PanelDisplayDriver.inf`: `UmdfExtensions=IddCx0102`

---

## Como compilar

```powershell
cd driver\PanelDisplayDriver
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

Compila os três projetos (o driver apenas se o WDK existir) e roda os testes.

Variações:

```powershell
# Release
powershell -File scripts\build.ps1 -Configuration Release

# Somente o driver (falha se o WDK não estiver instalado)
powershell -File scripts\build.ps1 -DriverOnly

# Sem rodar testes
powershell -File scripts\build.ps1 -SkipTests
```

Ou direto pelo MSBuild — note o `amd64`, ver "É obrigatório usar o MSBuild de
64 bits" acima:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
    PanelDisplayDriver.sln /p:Configuration=Debug /p:Platform=x64
```

Empacotar o resultado do driver em `dist/`:

```powershell
powershell -File scripts\package.ps1
```

O pacote gerado em `dist\Debug\` contém:

| Arquivo | Papel |
| --- | --- |
| `PanelDisplayDriver.inf` | INF com `DriverVer` e `UmdfLibraryVersion` já substituídos |
| `PanelDisplayDriver.cat` | catálogo assinado |
| `PanelDisplayDriver.dll` | binário do driver |
| `PanelDisplayDriver.cer` | certificado de teste gerado pelo WDK |
| `PanelDisplayDriver.pdb` | símbolos |

---

## Assinatura para desenvolvimento

> Estes passos alteram configurações de segurança da máquina e **exigem
> reinicialização**. Nenhum script deste repositório os executa. Faça-os
> conscientemente, de preferência em uma máquina de desenvolvimento ou VM.

Um driver de teste só carrega se as três condições valerem: o pacote está
assinado, o certificado de teste é confiável na máquina e ela está em modo test
signing.

**1. Certificado de teste — já feito pelo build.** O build em Debug gera um
certificado auto-assinado e com ele assina a DLL e o catálogo. Não é preciso
`makecert`. O certificado é copiado para o pacote como `PanelDisplayDriver.cer`:

```text
Assinante: CN="WDKTestCert <usuário>,<serial>"
```

Confirme com:

```powershell
Get-AuthenticodeSignature dist\Debug\PanelDisplayDriver.cat |
    Select-Object Status, @{n='Signer';e={$_.SignerCertificate.Subject}}
```

`Status` pode vir como `UnknownError` com a mensagem "cadeia terminou em um
certificado raiz não confiável" quando a máquina ainda não confia no
certificado. Isso **não** quer dizer que falta assinatura; depois de a confiança
ser configurada, o mesmo artefato deve aparecer como `Valid`.

**2. Confiar no certificado.** Instale `PanelDisplayDriver.cer` em *Trusted Root
Certification Authorities* e *Trusted Publishers* da máquina local. Isso amplia
o conjunto de editores em que o Windows confia — faça apenas com um certificado
que você mesmo gerou, e de preferência em uma máquina de desenvolvimento.

**3. Habilitar test signing** (Administrador, exige reboot):

```powershell
bcdedit /set testsigning on
```

Isso permite que a máquina carregue drivers não assinados pela Microsoft; a área
de trabalho passa a exibir a marca "Modo de teste". Para reverter:

```powershell
bcdedit /set testsigning off
```

Em máquinas com Secure Boot ativo, `testsigning` pode ser recusado. **Não
desative o Secure Boot como parte deste trabalho**; use uma VM ou uma máquina de
desenvolvimento dedicada.

---

## Instalação

Também manual. O script explica antes e só age com `-Confirm`.

```powershell
# 1. ver o que seria alterado (não altera nada)
powershell -File scripts\install.ps1

# 2. aplicar, em prompt de Administrador
powershell -File scripts\install.ps1 -Confirm
```

O script inventaria instâncias presentes e phantom via ConfigMgr, remove pelo
instance id completo quando necessário, espera o registro ficar `Absent`, faz
staging com `pnputil /add-driver` **sem** `/install` e só então cria o device.
Após a criação ele exige PnP saudável estável e `PanelDisplayCtl status` em
`Ready`.

O DevGen cria deliberadamente um **software device** por padrão; a instância é
`SWD\DEVGEN\PanelDisplayDriver`. Não é um device root-enumerated e o script não
usa `/bus ROOT`. `Root\PanelDisplayDriver` é apenas o hardware id do INF.

O script **não** altera Secure Boot, BCD, test signing nem armazenamentos de
certificado.

## Desinstalação

```powershell
powershell -File scripts\uninstall.ps1            # explica
powershell -File scripts\uninstall.ps1 -Confirm   # aplica
```

Remove o nó do dispositivo e o pacote do driver store. Se houver monitor
conectado, ele desaparece junto.

---

## Usando o PanelDisplayCtl

```powershell
PanelDisplayCtl status
PanelDisplayCtl connect
PanelDisplayCtl disconnect
```

Saída esperada:

```text
> PanelDisplayCtl status
PanelDisplayDriver: Ready
Virtual monitor: Disconnected

> PanelDisplayCtl connect
Virtual monitor connected.

> PanelDisplayCtl status
PanelDisplayDriver: Ready
Virtual monitor: Connected

> PanelDisplayCtl disconnect
Virtual monitor disconnected.
```

Comandos repetidos são idempotentes e retornam sucesso:

```text
> PanelDisplayCtl connect
Virtual monitor already connected.
```

Detalhes adicionais (modo ativo, contador de frames, lista de modos) ficam em
`status --verbose`; o `status` simples imprime exatamente as duas linhas acima,
para facilitar leitura por script.

Modo preferido explícito (é a base para o PanelHost informar a geometria real do
tablet):

```powershell
PanelDisplayCtl connect --mode 1920x1200@60
```

Códigos de saída: `0` sucesso, `1` erro de uso, `2` driver inacessível,
`3` o driver reportou falha.

Esta ferramenta é temporária. O PanelHost usará a mesma interface via
`tools/PanelDisplayCtl/DriverClient.*`.

---

## Verificando o monitor no Windows

Após `PanelDisplayCtl connect`:

- **Configurações → Sistema → Vídeo**: aparece um segundo monitor identificado
  como **PanelOS**.
- **Gerenciador de Dispositivos → Monitores**: aparece uma entrada nova.
- Linha de comando:

  ```powershell
  Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID |
      ForEach-Object { -join [char[]]($_.UserFriendlyName | Where-Object { $_ -ne 0 }) }
  ```

Após `PanelDisplayCtl disconnect` o monitor some das três verificações.

O nome "PanelOS" vem do bloco EDID em `driver/Edid.h`, gerado e validado por
`tools/Generate-Edid.ps1`:

```powershell
powershell -File tools\Generate-Edid.ps1 -Verify -EmitCpp
```

---

## Modos de vídeo

| Resolução | Taxa |
| --- | --- |
| 1280x800 | 60 Hz (preferido) |
| 1920x1080 | 60 Hz |
| 1920x1200 | 60 Hz |

1280x800 é um modo comum de tablet 16:10 e barato de codificar; 1920x1080
corresponde 1:1 à maioria dos desktops; 1920x1200 permanece disponível como
fallback de alta resolução. 1280x800 também é o *preferred detailed timing* do
EDID para evitar resize no pipeline padrão do tablet.

Validação (`common/PanelDisplayModes.h`): 640–3840 × 480–2160, 24–144 Hz,
dimensões pares. A exigência de dimensões pares existe porque todo encoder de
vídeo com croma 4:2:0 que o PanelHost usará depois exige isso — rejeitar aqui
evita publicar um modo que teríamos de recusar adiante.

---

## Testes

### Automáticos

```powershell
powershell -File scripts\build.ps1
```

Cobrem a lógica não-WDF: parsing de comandos, validação e tabela de modos, e a
máquina de estados (incluindo chamadas duplicadas, transições sobrepostas, falha
de criação e parada do dispositivo).

Resultado atual: **257 verificações, 0 falhas** (as 177 originais foram
preservadas). Há regressões específicas para dez ciclos e para
`connect/connect/disconnect/disconnect`.

O driver em si não tem cobertura automatizada — ele exige o dispositivo
instalado. O que o build garante é compilação limpa sob `/W4 /WX` mais a análise
estática de driver; o comportamento é verificado pelo procedimento manual
abaixo.

### Manual (requer driver instalado)

1. `PanelDisplayCtl status` → `Ready` / `Disconnected`.
2. Abrir Configurações → Vídeo. Confirmar que só há monitores físicos.
3. `PanelDisplayCtl connect` → `Virtual monitor connected.`
4. Confirmar o monitor **PanelOS** em Configurações → Vídeo.
5. `PanelDisplayCtl status` → `Connected`.
6. `PanelDisplayCtl connect` de novo → `Virtual monitor already connected.`,
   e **apenas um** monitor virtual continua existindo.
7. Estender a área de trabalho para o monitor PanelOS; `status --verbose` deve
   mostrar o modo ativo e o contador de frames subindo.
8. `PanelDisplayCtl disconnect` → `Virtual monitor disconnected.` e o monitor
   desaparece.
9. `PanelDisplayCtl disconnect` de novo → `Virtual monitor already disconnected.`
10. Executar pelo menos dez ciclos completos de connect/disconnect.
11. Executar `connect`, `connect`, `disconnect`, `disconnect`.
12. Confirmar que há um único adapter PanelOS, nenhum monitor PanelOS ativo após
    o último disconnect, nenhum Code 43 e nenhum restart do host UMDF.

---

## Coletando logs

O driver usa TraceLogging com o provider `PanelOS.PanelDisplayDriver`
(`{7FD77C51-84BD-4C77-B668-9A7C64C893BD}`). Não há WPP, então não é preciso TMF
nem PDB para ler os eventos.

```powershell
# capturar
tracelog -start PanelOS -guid #7FD77C51-84BD-4C77-B668-9A7C64C893BD -f panel.etl -flag 0xFFFF -level 5

# reproduzir o cenário (connect / disconnect)

tracelog -stop PanelOS
tracerpt panel.etl -o panel.xml -of XML
```

Alternativa em builds Debug: checkpoints críticos também vão para a saída do
depurador. Abra o **DebugView** (com *Capture Global Win32*) e filtre por
`[PanelDisplayDriver]`; o conjunto completo continua no TraceLogging.

Eventos registrados: carga do driver, criação e prontidão do adapter, connect
solicitado, criação do monitor, arrival, modo selecionado, início/parada do
swapchain, disconnect solicitado, departure, e os erros de IddCx/WDF relevantes.

---

## Troubleshooting

**`PanelDisplayDriver: Not found`**
O driver não está instalado ou o nó do dispositivo não existe. Verifique o
Gerenciador de Dispositivos; recrie o nó com `devgen` (ver Instalação).

**`Failed to open ... Win32 error: 5 (ERROR_ACCESS_DENIED)` com `Process elevated: yes`**

Se o processo **está** elevado e mesmo assim recebe acesso negado, elevação não
é o problema: o pacote instalado não tem descritor de segurança concedendo
acesso a Administrators.

Sintoma característico: o `CreateFile` falha **até com máscara de acesso 0**, o
que descarta a hipótese de "bits de acesso errados" — a DACL está recusando a
abertura inteira, não apenas certos direitos.

Diagnóstico rápido (não requer elevação):

```powershell
Get-PnpDeviceProperty -InstanceId "SWD\DEVGEN\PANELDISPLAYDRIVER" `
    -KeyName DEVPKEY_Device_SecuritySDS
```

Se o valor vier **vazio**, nenhum descritor foi aplicado ao devnode e este é o
problema. O pacote a partir da v2 do protocolo define `HKR,,Security` na seção
`.HW` do INF — ver "Segurança do dispositivo".

> **Reinstalar é obrigatório.** O descritor de segurança é aplicado ao devnode
> quando o nó do dispositivo é criado. Atualizar o driver no lugar **não** o
> aplica. É preciso remover o nó e recriá-lo:
>
> ```powershell
> powershell -File scripts\uninstall.ps1 -Confirm
> powershell -File scripts\install.ps1 -Confirm
> ```
>
> Confirme depois que `DEVPKEY_Device_SecuritySDS` passou a retornar
> `D:P(A;;GA;;;SY)(A;;GA;;;BA)`.

Note também que a v2 mudou os bits de acesso dos IOCTLs, o que altera os valores
numéricos dos control codes: driver e `PanelDisplayCtl` precisam ser
reconstruídos e instalados juntos.

A v3 mantém esses três IOCTLs de lifecycle intactos e adiciona os dois IOCTLs de
frame; PanelHost, PanelDisplayCtl e driver ainda devem usar o mesmo `Public.h`.

**`Failed to open ...` com `Process elevated: no`**
Rode o `PanelDisplayCtl` em prompt elevado. Se continuar falhando elevado, veja
o item acima.

**O dispositivo aparece com erro (triângulo amarelo) no Gerenciador de Dispositivos**
Leia primeiro o problem code: código 52 aponta assinatura; código 31 indica que
o driver não carregou; código 43 (`CM_PROB_FAILED_POST_START`) é falha real após
o início e exige SetupAPI, eventos UMDF e tracing do driver. Só investigue
certificado/test signing como causa quando o código e os logs apontarem para
isso.

**`status` mostra `Initializing` e o connect retorna "adapter não pronto"**
`EvtIddCxAdapterInitFinished` ainda não chegou ou falhou. Colete os logs e
procure `Adapter.InitAsyncFailed` ou `Adapter.InitFinishedFailed`.

**O monitor aparece mas fica preto / a resolução não é oferecida**
No milestone First Pixels, tela preta indica que o monitor foi criado, mas o
pipeline de frame ainda não publicou, leu ou renderizou um snapshot. Verifique
os logs `[Video]`, os dois ADB reverses e se o PanelHost está elevado quando a
ACL instalada exigir. Se a resolução não aparece, compare a lista em
`status --verbose` com os modos do EDID; os dois precisam concordar (ver
`tools/Generate-Edid.ps1`).

**`devgen failed with exit code 1460` / `Device creation failed: 0x000005B4`**

`0x5B4` é `ERROR_TIMEOUT`, não um diagnóstico de causa. No incidente de agosto
de 2026 ocorreram dois problemas distintos em sequência: o uninstall usava
`devgen /remove /instanceid ...`, mas `/instanceid` só é válido em `/add`, e o
registro SWD permaneceu. Em seguida, `pnputil /add-driver ... /install`
reiniciou esse devnode antes do `devgen /add`; o restart entrou realmente em
`CM_PROB_FAILED_POST_START` (Code 43). O `/add` redundante apenas esperou esse
start e expirou.

Um phantom sozinho não prova conflito nem explica Code 43. Rode o install em
explain mode para obter estado, problem code, pacote e todos os nós coincidentes:

```powershell
powershell -File scripts\install.ps1
```

`CM_PROB_PHANTOM` (Code 45) significa não presente; o script usa a operação PnP
suportada e exige que uma remoção solicitada chegue a `Absent`. Ele não apaga
`HKLM\SYSTEM\CurrentControlSet\Enum`.

A causa real de uma falha de instalação aparece em `C:\Windows\INF\setupapi.dev.log`
(precisa de elevação para ler):

```powershell
Select-String -Path C:\Windows\INF\setupapi.dev.log -Pattern 'PANELDISPLAY' |
    Select-Object -Last 40
```

**Monitor fantasma em `DISPLAY\PNL0001\...`**

Um nó `DISPLAY\PNL0001\...` com status `CM_PROB_PHANTOM` é apenas o registro do
monitor virtual de uma sessão anterior. Phantom significa "não presente"; não é
um monitor ativo e não conta como monitor duplicado.

**O primeiro disconnect retorna `0x8007050B`**

Esse HRESULT significa que o processo que hospedava o driver terminou. O dump
histórico mostrou uma exceção do WUDFHost no caminho que apagava novamente o
`IDDCX_MONITOR` após `IddCxMonitorDeparture`. O lifecycle atual não faz esse
double-delete. Se reaparecer, trate como crash UMDF e preserve o dump; não
repita o comando como se fosse somente uma transição lógica.

**`error MSB8020 ... WindowsUserModeDriver10.0`**
O WDK não está instalado, ou está integrado a outra instalação do Visual Studio
que não a usada no build. Veja "Duas instalações do Visual Studio".

**`error : INF verification exception: ... 'x86\InfVerif.dll'`**
Build feito com o MSBuild de 32 bits. Use o de `Bin\amd64`. Atenção: esse erro
**não** derruba o build (exit code continua 0), então o pacote sai sem
verificação de INF. Veja "É obrigatório usar o MSBuild de 64 bits".

---

## Limitações

Propositalmente **não** implementado nesta versão:

- fila de frames ou espera do swapchain por PanelHost/USB;
- encoder de vídeo (H.264/HEVC/AV1) e gravação em arquivo;
- USB, ADB, TCP, WebSocket ou qualquer protocolo Android;
- touch, entrada, ou qualquer UI;
- múltiplos monitores virtuais (limite de 0 ou 1 é intencional);
- HDR, gama, I2C, OPM/HDCP;
- negociação dinâmica completa de modo. `--mode` já valida e prioriza um modo
  fornecido de fora, mas a lista de modos do monitor vem do EDID estático; um
  modo realmente novo exige regenerar o EDID (`tools/Generate-Edid.ps1`). Essa é
  a próxima costura a fechar quando a geometria real do tablet for conhecida.

Uma versão anterior já foi instalada e comprovou load, interface, status,
arrival e monitor virtual real. Cada pacote reconstruído ainda precisa passar
pela instalação elevada limpa e pelo teste manual de dez ciclos; build e testes
unitários não substituem essa validação de integração.

---

## Próxima etapa

Concluir a instalação elevada limpa e o teste manual de dez ciclos descrito
acima. A integração com PanelHost só deve começar depois que esse pacote passar
sem timeout, Code 43, restart do UMDF ou duplicação de adapter/monitor.
