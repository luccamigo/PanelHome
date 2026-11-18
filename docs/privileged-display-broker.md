# PanelOS — Privileged Display Broker

## Existing privilege boundary

Antes desta mudança, `PanelHost.exe` era executado elevado para alcançar a DACL
do `PanelDisplayDriver`. O management v1 era então criado por um processo High
IL, e o `PanelOS.ControlCenter.exe` Medium IL podia receber
`ERROR_ACCESS_DENIED`. O Host também fazia diretamente todas as operações de
lifecycle, captura e métricas do driver.

`PanelOS.DriverInterop` já existia como projeto independente e continua sendo a
única implementação do contrato Win32/IOCTL. `PanelDisplayCtl` não é iniciado
nem usado como IPC.

## New architecture

```text
PanelOS.ControlCenter (Medium IL)
        ↕ PanelHost management v1 / Windows user SID ACL
PanelHost (Medium IL, as-invoker)
        ↕ PanelDisplayBroker Protocol v1 + shared frame mailbox
PanelDisplayBroker (High IL, runas)
        ↕ PanelOS.DriverInterop / IOCTL
PanelDisplayDriver
```

O Control Center agora compõe `PanelHostManagementDriverController`, portanto
não abre o device interface. `PanelHost` cria o endpoint antes da elevação e
somente inicia o broker quando uma operação Desktop realmente precisa do
driver. O consentimento UAC é, assim, restrito ao broker.

O broker não referencia Panel Protocol, PLMX, H.264, Stream Deck, URLs, apps ou
launchers de shell. Seu switch aceita somente o enum fechado de operações do
driver. Não há executor genérico, PowerShell ou caminho de comandos arbitrários.

## Broker protocol

`PanelDisplayBroker Protocol v1` é um protocolo local independente de
management v1. O controle usa mensagens UTF-8 delimitadas por LF, limitadas a
64 KiB, com `protocolVersion`, `requestId` e operação tipada. Respostas devem
repetir versão e request id.

Operações v1:

| Operação | Frequência | Transporte |
| --- | --- | --- |
| status | sob demanda | control IPC |
| connect | uma vez por Desktop start | control IPC |
| disconnect | uma vez por Desktop stop/lease end | control IPC |
| get frame info | contínua | fica dentro do broker |
| read latest frame | contínua | fica dentro do broker |
| get capture metrics/pacing | janela de métricas | control IPC |
| set capture target | start/negociação 30/45/60 | control IPC |

JSON é usado somente neste plano de controle pequeno. Pixels nunca são
serializados nele e stdout/stderr não participam do IPC.

## IPC security

O endpoint segue a direção Medium → High: `PanelHost` Medium cria um named pipe
com nome aleatório, `PipeOptions.CurrentUserOnly` e uma única instância; o
broker elevado conecta como cliente. A identidade retornada pelo pipe deve ser
igual à identidade Windows do Host.

Para cada launch, o Host também cria um nonce CSPRNG de 256 bits. O hello valida
em tempo constante nonce, protocol version, parent PID e parent start time. O
autenticador é single-use, de modo que replay/stale nonce falha. O nome do pipe
e o nome da memória mapeada usam GUIDs independentes. O Host registra somente
`nonce=<redacted>`.

Não existe pipe público/global nem DACL para `Everyone`. Objetos são locais à
sessão, criados pelo usuário Medium antes do UAC; High IL pode escrever em um
objeto Medium do mesmo usuário, enquanto o caminho inverso que causava o bug
de integrity level foi removido. Elevação com credenciais de outro usuário é
intencionalmente rejeitada pela restrição de identidade.

## Frame transport

Uma mailbox nomeada de memória mapeada possui capacidade fixa de 64 MiB para um
payload, mais 128 bytes de header. Ela implementa uma fila de profundidade um:
o frame mais novo sobrescreve o anterior. O header contém layout version,
seqlock, valid bit, generation, sequence, QPC timestamp/frequency, width,
height, stride, format, payload length e contadores published/overwritten.

O writer único publica com seqlock ímpar/par; o reader copia metadados/pixels e
aceita o snapshot somente se o seqlock permanecer igual e par. Uma leitura de
um frame já sobrescrito retorna `FrameReplaced`, preservando o retry limitado
existente. `Reset` invalida a geração ao desconectar. Não há fila ilimitada,
backpressure acumulativa ou cópia de frame pelo named pipe.

O frame pump fica inativo enquanto o monitor está desconectado. Durante captura
ele solicita resolução de timer de 1 ms no próprio processo do broker e a libera
ao desativar, permitindo polling acima de 60 Hz sem manter o custo de energia
durante todo o lifecycle do Host. Enable/reset/publish compartilham uma trava:
um frame em voo nunca pode reaparecer depois do reset de disconnect.

## Driver operations moved

Todas as sete classes de operação foram movidas para o processo elevado:
status, connect, disconnect, get frame info, read latest frame, capture
metrics/pacing e capture target. `PanelDisplayDriverClient(keepHandleOpen:
true)` mantém um handle read/write durante a lease do broker. O Host vê apenas
o proxy tipado e a mailbox.

Sem o driver instalado, abrir o Desktop continua retornando `driver_not_found`.
Ao tocar em **Voltar ao painel**, a ausência do driver é tratada como Desktop
já encerrado: PanelHost publica `stopped`, permitindo voltar à página inicial.
Com o driver presente, erros de permissão, comunicação ou I/O só continuam
sendo falhas de encerramento se o broker permanecer conectado. Se a conexão
do broker desaparecer, o encerramento local publica `stopped`, como descrito
na seção PAN-4.

No catch de Stop, `NotFound` é tratado antes de `disconnectIssued`: a ausência
do driver já encerra a sessão local, inclusive se ocorrer no Disconnect ou na
consulta de confirmação. Não há novo Disconnect de compensação nesse caso,
pois não existe driver para receber o comando. Start continua recusando
`driver_not_found`. Essa regra incorpora a correção local do mantenedor sobre a
correção de perda do broker em uma correção anterior.

## PanelHost privilege after

`PanelHost.exe` não possui manifesto `requireAdministrator` e é iniciado pelo
Control Center com `UseShellExecute=false`, portanto preserva o token Medium do
caller. Somente `PanelDisplayBroker.exe` é iniciado com `Verb=runas`.

Se o broker morre ou o pipe fecha, o proxy emite
`PanelDisplayBrokerDisconnectedEventArgs` com reason tipado. Desktop muda para
`Error/broker_disconnected`; o watcher de vídeo encerra a sessão porque o
Desktop deixou de estar `Running`; management e os demais serviços do Host
continuam vivos.

Falhas no início do broker (Start do Desktop):

- O operador não aprova (ou fecha) o UAC: `Win32Exception 1223` vira
  `PanelDisplayBrokerDisconnectReason.ElevationDeclined` e o Desktop responde
  `Error/elevation_declined`. O Control Center mostra "A elevação não foi aprovada...".
  Tablets antigos tratam o código desconhecido como erro genérico.
- O broker conecta e derruba o pipe antes de concluir o handshake (por exemplo, sai em
  menos de 1 s): `IOException`/`ObjectDisposedException` viram
  `PipeClosed` (ou `BrokerExited`, quando o código de saída foi lido) e o Desktop responde
  `Error/broker_disconnected`, não `driver_io_error`. O host registra o código de saída do broker
  (`[DisplayBroker] ... process exit code N`): 2 argumentos, 3 sem elevação, 4 autenticação,
  5 parent indisponível, 6/7 watchdog.
  - **Código 0 é o esperado quando o broker perde o pipe no handshake:** o broker engole
    `IOException`/`JsonException` nesse ponto, executa o `finally` (até 3 s) e sai normalmente.
    Código 0 aqui significa "o broker fechou a conexão por conta própria", não sucesso.
  - Uma exceção não tratada do .NET termina o processo com código de crash (por exemplo
    `-532462766`/`0xE0434352`) e deixa evento no log Application do Windows.
  - O Host não consegue ler o código se o processo continuar vivo (espera 1 s e registra
    "exit code unavailable"). Não foi provado que um Host em integridade Medium leia o código de um
    broker elevado; se aparecer "unavailable" mesmo com o broker morto, é esse limite.
- Depois de qualquer erro de Start, o botão "Conectar" do Control Center continua disponível
  (o estado `Error` do Desktop não é mais tratado como driver ausente).

Se o Host morre, o kernel fecha o pipe. O broker usa esse pipe como lease e
também mantém um handle para o parent PID validado pelo start time. O primeiro
entre EOF do pipe e saída do parent cancela o frame pump; no `finally`, o broker
invalida a mailbox, tenta disconnect limitado a três segundos e termina. Não há
listener de reconexão que possa manter um broker órfão.

## Control Center management test

Management v1 usa dono e DACL explícitos para o SID do usuário Windows no
servidor, com `ReadWrite | CreateNewInstance`; o cliente mantém `CurrentUserOnly`.
Isso evita usar Administrators como dono/DACL quando o Host é elevado. A
validação isolada Medium → High passou na segunda rodada UAC, descrita abaixo;
High → Medium continua sem suporte adicional no cliente. O broker e
suas regras de IPC permanecem independentes deste ajuste. Além da
subscription/snapshot, os comandos `desktop_start` e `desktop_stop` entram no
mesmo `IDesktopService` usado pelo Panel Protocol; eles não expõem IOCTL bruto.
O teste de integração cobre Host, tablet/control+video, display, métricas de
vídeo e Stream Deck pelo pipe, além de start/stop pelo lifecycle.

### Validação PAN-1 com elevação — 07/11/2026

Rodada de validação, executada pela coordenação após autorização
do mantenedor.
Manifesto SHA-256 conferido:
`E9E33B4020E51D67BD3345DE7CC141A68FC1C00C2356D3634B4EBB494D7F7D1D`.
Os logs brutos da rodada ficam arquivados fora do repositório.

| Verificação | Resultado observado nos logs |
| --- | --- |
| Dono/DACL do pipe criado pelo processo elevado | `PAN-1-owner`: `setOwnerAccepted=true`, `userOnlyLimitedAcl=true`; dono igual ao SID do usuário solicitado. O processo elevado aceitou `SetOwner(usuário)` |
| Cliente normal → servidor elevado | `PAN-1`: `result=snapshot`, `expectedPid=actualPid=37520` |
| Cliente elevado → servidor normal | `UnauthorizedAccessException`, HRESULT `0x80070005`, em `NamedPipeClientStream.ValidateRemotePipeUser`. Era observação, não critério de aprovação; o cliente não foi alterado para suportar esse sentido |
| Segundo guard, nos dois sentidos | `PAN-2`: `acquired=false`, sem exceção do guard |
| Encerramento do harness | Os três papéis registraram `role_finished`; os dois servidores também registraram `server_stopped`. A coordenação informou saída 0 dos processos normal/elevado |
| Instâncias já em uso | `existing-before.json` e `existing-after.json` são idênticos: Host e Control Center em execução, com mesmos caminhos e datas de criação |

O harness usou servidor, cliente e guard reais do produto com nomes de pipe/mutex
exclusivos por GUID e `FakeDesktopService`. Não iniciou o `PanelHost.Main`, não
abriu o pipe de produção, portas de transporte ou driver e não interrompeu as
instâncias em uso. O resultado valida a correção de dono/DACL e o snapshot
normal → elevado nesse isolamento; não substitui teste do Host/Control Center
completos em operação. Não houve nova execução elevada para esta atualização
documental.

PAN-2 permanece limitado ao guard na mesma conta/sessão: `equivalentApplicationExit=17`
é derivado, não a saída de um segundo `PanelHost.exe` completo. Não foi
demonstrado crash no startup completo nem testado outro logon. A decisão de
suportar cliente elevado → servidor normal segue pendente.

## Second-host handling

Um mutex nomeado por usuário é adquirido antes da inicialização de telemetry,
media, transport ou management. Uma segunda instância escreve uma mensagem
clara, não instala o crash handler e termina com exit code `17`, sem stack
trace. O handle do mutex é a lease do processo e funciona mesmo que a
continuação async mude de thread.

## Performance before/after

Baseline físico anterior, registrado em
[`benchmarks/transport/gate-ledger.md`](benchmarks/transport/gate-ledger.md):

| Métrica | Antes (Host elevado/direto) | Depois (broker) |
| --- | ---: | ---: |
| driver publish FPS | 60,0 em janelas completas | validação física pendente |
| host receive FPS | 60,0 em janelas completas | validação física pendente |
| send FPS | 60,0 em janelas completas | validação física pendente |
| capture-to-send | 24,3–27,1 ms | validação física pendente |
| CPU | registrar Host e broker separadamente | validação física pendente |
| memória | registrar working set Host + broker | mailbox fixa: 64 MiB + 128 B; validação física pendente |

O resultado pós-mudança não é inventado em CI sem driver/tablet. A instrumentação
existente continua medindo driver capture, host receive, send e
capture-to-send; a campanha física deve também capturar CPU/working set dos dois
PIDs e somá-los para a comparação. O critério é manter 60 FPS sem aumento
material de latency, CPU ou memória além da mailbox bounded.

## Tests

Cobertura automatizada adicionada:

- round-trip e rejeição de operação desconhecida no broker v1;
- nonce incorreto, nonce consumido e stale nonce;
- host pipe EOF como fim da lease;
- broker disconnect tipado no Desktop lifecycle;
- shared-memory generation, reset, overwrite e latest-frame-wins;
- segunda instância e reutilização após saída;
- management Medium → Medium, snapshot completo e Desktop start/stop.

O driver físico permanece fora de CI. Testes existentes de lifecycle,
frame-source, vídeo, management e Control Center continuam sendo a regressão do
restante do pipeline.

## Builds

`PanelDisplayBroker` e `PanelOS.DisplayBroker.Protocol` fazem parte de
`PanelOS.sln`. O build de `PanelHost` depende do broker e copia apphost, DLL,
deps/runtimeconfig e as duas DLLs compartilhadas para o diretório do Host.

Comandos de validação:

```powershell
dotnet build windows\PanelOS.sln -c Debug
dotnet test windows\PanelHost.Tests\PanelHost.Tests.csproj -c Debug
dotnet test windows\PanelOS.ControlCenter.Tests\PanelOS.ControlCenter.Tests.csproj -c Debug
```

## Physical validation required

Em uma máquina com o driver instalado e um tablet conectado:

1. confirmar no Process Explorer que Control Center e PanelHost estão Medium IL
   e somente PanelDisplayBroker está High IL;
2. iniciar Desktop e confirmar um único prompt UAC para o broker;
3. observar Dashboard/Diagnostics durante Desktop e registrar snapshot completo;
4. sustentar conteúdo dinâmico 1280×800 a 60 FPS e coletar as seis métricas da
   tabela before/after, incluindo os dois processos;
5. encerrar o broker e confirmar Desktop/video teardown com management vivo;
6. encerrar o Host e confirmar que o broker desconecta o monitor e termina;
7. tentar um segundo Host e confirmar mensagem + exit code 17.

## Remaining owner/lease work

A lease atual é exclusiva por launch e termina com parent/pipe. O contrato do
driver não foi alterado. Um futuro protocolo do driver pode materializar owner
ID, lease generation, timeout/renew e recuperação explícita após crash duro do
broker. Isso deve ser versionado no driver/DriverInterop e não deve reintroduzir
Panel Protocol ou comandos genéricos no broker.

## Return to Panel after broker failure (PAN-4)

`desktop.stop` publishes `stopping` then `stopped` when the broker connection is
already absent, including after a failed start or declined elevation. It does
not start another broker or request elevation to query/disconnect an absent
lease. Status queries during stop (including compensating disconnect) use only
the existing connection and recheck it after waiting for the request lock.
If the connection disappears during stop, local Desktop teardown completes;
an I/O failure from a broker that remains connected still reports an error.

The existing Android coordinator returns to Dashboard and clears the Desktop
error when this `stopped` state arrives. No new Android UI behavior or protocol
field is required. A broken control pipe may also fail the writer's final
flush; that expected failure no longer aborts disposal of the remaining
connection resources.

`stopped` here confirms the local Desktop session has ended. It does not prove
that a monitor left behind by a crashed broker was physically disconnected;
driver lease recovery remains the separate work described above. Unit tests
cover failed start, synthetic elevation cancellation, loss during stop,
cancellation, compensating stop, no relaunch, wire state, and Android return to
Dashboard. They do not launch an elevated broker or exercise a physical tablet.

The PAN-4 trace establishes `BrokerExited`, a broken pipe, and repeated
`driver_io_error` on stop. It does not establish why the broker exited. UAC
decline is unconfirmed; the elevated broker's `CurrentUserOnly` connection to
the normal host is another identity-check hypothesis requiring separate
evidence. This fix changes neither pipe authorization nor elevation policy.

## PAN-3 — broker travado e monitor residual

**Resíduo.** O driver não tem callback de cleanup do handle: o monitor criado por `CONNECT`
continua `Connected` depois que o dono some. Se o broker morrer ou for encerrado sem passar por
`DISCONNECT`, o monitor fica sem dono (visível em Configurações de vídeo do Windows).

**Causa do "travado" (leitura do código, sem broker real).**

- `PanelDisplayDriverClient` espera cada IOCTL com `completionEvent.WaitOne()`, sem prazo, mesmo
  depois do `CancelIoEx`. Se o driver não completa a chamada, a thread fica presa; as tarefas do
  broker nunca terminam e o `finally` com o `DISCONNECT` de limpeza não é alcançado.
- Enquanto uma chamada está presa o broker não lê o pipe, então não percebe que o Host o fechou.
- O Host esperava a resposta sem limite, segurando `_requestLock`: nenhum Stop/status passava.
- O Host (Medium) não consegue encerrar o broker (High); `Process.Kill` falha em silêncio.

**Correções.**

- Host: cada requisição tem prazo (15 s; `PanelDisplayBrokerClient.DefaultRequestTimeout`). Estourar
  fecha o pipe (sinal de lease), publica `PanelDisplayBrokerDisconnectReason.Unresponsive`
  (`broker_disconnected` no Desktop) e libera o lock; cancelamento do chamador continua distinto.
  Falha ao encerrar o processo agora é registrada no log.
- Broker: `BrokerWatchdog` roda em thread própria (não depende do thread pool). Uma chamada ao driver
  acima de 20 s encerra o processo (exit 7); depois de iniciar o encerramento, 10 s no máximo (exit 6).
  Esperas das tarefas durante o encerramento passam a ter limite de 2 s, para o `DISCONNECT` limitado a
  3 s ainda rodar quando possível. Sair fecha o handle do driver e o pipe.

**Recuperação do monitor residual (sem novo UAC escondido).** O Host não relança o broker só para
limpar (regra do PAN-4). O monitor residual é adotado pelo próximo Start explícito (`alreadyRunning`,
sem segundo CONNECT) e removido pelo Stop seguinte; coberto por teste com o driver simulado.

**Limites.** Se o driver estiver de fato travado, nem o `DISCONNECT` do broker funciona; o watchdog só
garante que o processo não fique pendurado. Remover o monitor sem dono automaticamente exige um callback
de cleanup no driver (`EvtFileCleanup` ligado ao handle que fez CONNECT): mudança de driver que precisa de
build/assinatura/instalação e teste físico, fora desta entrega. Não foram executados broker real, UAC,
driver, tablet nem instalação.
