# Recuperação do Native USB no Android

Este documento descreve o ciclo de vida físico do carrier nativo no PanelHome:
o que acontece quando o cabo sai, quando o cabo volta, e quando o PanelHost
reinicia sem que o cabo saia. Os três casos são diferentes e a diferença é o
motivo deste documento existir.

## O problema que isto resolve

O PanelHome é o app HOME do tablet. Até 2026-08-27 a única recuperação conhecida
para um cable pull era **reiniciar o processo** — `Runtime.getRuntime().exit(0)`
em `MainActivity.observeNativeUsbRecovery`, registrado em
[`transport-device-limitations.md`](transport-device-limitations.md). Isso
funcionava, mas em um app HOME significa a tela inicial do tablet desaparecendo e
o Android relançando o launcher.

Além disso, o cable pull **travava o app**. A causa foi identificada nesta
tarefa e tinha dois componentes independentes:

1. `ACTION_USB_ACCESSORY_DETACHED` chega no `BroadcastReceiver`, ou seja, na
   main thread. O handler antigo chamava `restartCurrentSession()` de forma
   síncrona, o que fechava os canais lógicos, e o fechamento de um canal
   enfileirava um FIN e **esperava sem limite** a writer thread colocá-lo no fio.
   Depois de um cable pull essa writer thread está dentro de um `write()` que não
   retorna. A main thread esperava junto: ANR.
2. Um detach era tratado como uma falha de sessão qualquer. O transporte
   reabria o accessory imediatamente e o laço de reconexão do control channel
   continuava chamando `openAccessory()` a cada 1–5 s — contra um accessory que
   não existe, porque o cabo tinha acabado de sair.

## Máquina de estados

```text
                    ┌──────────┐
                    │ DISABLED │  carrier = ADB/TCP
                    └────┬─────┘
                         │ accessory attached
                         ▼
  ┌──────────────────────────────┐   permission missing   ┌───────────────────────┐
  │   WAITING_FOR_ACCESSORY      │──────────────────────► │ WAITING_FOR_PERMISSION│
  │  sem descriptor, sem timer   │ ◄──────────────────────│  um único diálogo     │
  └───────┬──────────────────────┘        denied          └───────────┬───────────┘
          │ ATTACHED                                                  │ granted
          ▼                                                           │
  ┌───────────────────┐   openAccessory refused   ┌─────────┐         │
  │ OPENING_ACCESSORY │ ────────────────────────► │ BACKOFF │ ◄───────┘
  │   single-flight   │ ◄──────────────────────── │ 250ms→4s│
  └───────┬───────────┘        retry timer        └─────────┘
          │ descriptor aberto
          ▼
  ┌────────────────────────┐
  │ TRANSPORT_HANDSHAKING  │  PLMX v2 HELLO / HELLO_ACK
  └───────┬────────────────┘
          │ sessão PLMX estabelecida
          ▼
  ┌──────────────────────────┐ ◄──── HELLO mais novo (host restart)
  │ APPLICATION_HANDSHAKING  │       o descriptor NÃO é fechado
  └───────┬──────────────────┘
          │ Panel Protocol v1 hello_ack
          ▼
  ┌───────────┐   DETACHED    ┌────────────────────┐
  │ CONNECTED │ ────────────► │ CLOSING_ACCESSORY  │ ──► WAITING_FOR_ACCESSORY
  └───────────┘               └────────────────────┘
          │ falha de transporte com o cabo presente
          ▼
  ┌─────────────────────┐
  │ RECOVERING_SESSION  │ ──► (close) ──► OPENING_ACCESSORY
  └─────────────────────┘
```

`STOPPED` é terminal e só é alcançado por `ServiceStopping`.

A máquina é um redutor puro em
[`UsbLifecycleStateMachine.kt`](../app/src/main/java/com/panelos/panelhome/session/UsbLifecycleStateMachine.kt):
ela não toca em Android, descriptor, corrotina nem relógio que não tenha
recebido. Quem executa as ações é
[`UsbLifecycleController`](../app/src/main/java/com/panelos/panelhome/session/UsbLifecycleController.kt),
que consome um único `Channel` de eventos — um evento por vez, sempre.

### Por que um único laço serializado

Antes, quatro coisas mudavam o estado do USB de forma independente e nenhuma
sabia das outras: o `BroadcastReceiver`, o `onStartCommand`, o timer de recovery
do runtime e o laço de reconexão do transporte. Um cable pull podia ser tratado
duas vezes, ou tratado enquanto um teardown ainda estava rodando. O descriptor
que sobrevivia a essa corrida era o motivo de a reconexão exigir um processo
novo.

Hoje **nada além do controller abre ou fecha um accessory**, e o
`openAccessory` acontece dentro do laço — o que torna o single-flight estrutural
e não uma convenção.

## Host restart não é detach

| | Host restart | Physical detach |
| --- | --- | --- |
| Evento que manda | `HELLO` mais novo no PLMX | `ACTION_USB_ACCESSORY_DETACHED` |
| Descriptor | **preservado** | fechado |
| Sessão PLMX | readotada (*newest HELLO wins*) | encerrada |
| Canais lógicos | resetados | fechados |
| Estado final | `APPLICATION_HANDSHAKING` → `CONNECTED` | `WAITING_FOR_ACCESSORY` |
| Reabre accessory | não | só depois de um novo `ATTACHED` |

Um cabo que continua conectado não produz `ATTACHED`, então fechar o descriptor
em um host restart transformaria uma reconexão de dois segundos em uma
recuperação física que ninguém vai completar. `PanelSessionCoordinator` já
reconecta o Panel Protocol sozinho nesse caso; a máquina apenas rearma o prazo do
handshake da aplicação e não toca no carrier.

O mesmo vale para uma sessão do Panel Protocol que cai sem o PLMX cair: o
carrier é preservado e o prazo de 15 s é o que decide se aquilo virou uma
reciclagem.

## Ownership do descriptor

[`AccessoryHandle`](../app/src/main/java/com/panelos/panelhome/data/transport/AccessoryHandle.kt)
é o dono único.

- **single owner:** `AccessoryHandle` fecha o `ParcelFileDescriptor`; nada mais
  fecha. O `FileInputStream` e o `FileOutputStream` são construídos sobre o mesmo
  `FileDescriptor` e **não têm ciclo de vida próprio**.
- **double-close protection:** um `AtomicBoolean` com `compareAndSet`. Fechar o
  mesmo fd duas vezes pode cair em um número de descriptor que o runtime já
  reciclou, e é assim que um `openAccessory` posterior passa a recusar para
  sempre. Coberto por `AccessoryHandleTest`, inclusive com três threads
  fechando ao mesmo tempo (detach + watchdog + teardown da sessão).
- **flush:** nenhum, em nenhum caminho de teardown. Depois de um cable pull um
  write para o gadget não retorna, então o fechamento do descriptor é toda a
  fronteira de teardown.
- **watchdog:** `HandshakeWatchdog` continua fechando o descriptor quando o
  handshake passa de 12 s. Ver a limitação medida abaixo.

`NativeUsbTransport.close()` **não pega nenhum lock**. Isso não é um detalhe:
veja a medição abaixo.

## Generations

Toda tentativa de abertura física incrementa `generation`. Todo relatório
assíncrono — sucesso de handshake, perda física, resultado de open, fim de
teardown — carrega a generation a que pertence, e o redutor descarta o que não
for da generation viva, contando em `usbStaleGenerationEventsIgnored`.

É isso que garante que um erro tardio da sessão A não derrube a sessão B. Na
prática o caso comum é uma leitura bloqueada que retorna um minuto depois de a
sua sessão ter sido substituída.

## Timeouts e backoff

Todos em `UsbLifecycleTimings`:

| Constante | Valor | Limitado por |
| --- | --- | --- |
| `OPEN_ACCESSORY_TIMEOUT_MILLIS` | 3 s | `openAccessory` é uma chamada binder; não bloqueia no fio |
| `TRANSPORT_HANDSHAKE_TIMEOUT_MILLIS` | 12 s | backstop do limite de resync do PLMX (10 s) |
| `APPLICATION_HANDSHAKE_TIMEOUT_MILLIS` | 15 s | start-up do PanelHost, incluindo o display broker |
| `PHYSICAL_WRITE_TIMEOUT_MILLIS` | 5 s | uma escrita física; a writer thread é única |
| `OPEN_BACKOFF_MILLIS` | 250 ms → 4 s | rajada limitada enquanto o accessory parece presente |
| `MAX_CONSECUTIVE_OPEN_ATTEMPTS` | 6 | fim da rajada |
| `IDLE_REPROBE_MILLIS` | 30 s | uma tentativa rara, para sempre (ver limitação) |
| `IMMEDIATE_SESSION_RECYCLES` | 3 | um replug precisa reconectar na hora; um host ausente não |

**Detached não usa backoff de open.** Em `WAITING_FOR_ACCESSORY` não há timer
nenhum: não existe accessory para abrir, e o que acorda a máquina é um
`ATTACHED` real.

## Limitações medidas no tablet de teste (2026-08-27)

As duas foram observadas com o PanelHost **parado** e o tablet ainda em modo
accessory de uma sessão anterior. Não são o caminho normal de um replug, mas são
reais e o app tem de sobreviver a elas.

### Uma leitura bloqueada não é interrompida por fechar o fd

`ParcelFileDescriptor.close()` não desbloqueia uma thread parada dentro de
`read()` em `/dev/usb_accessory` neste aparelho. Medido: o watchdog fechou o
descriptor às 10:57:58.977 e a leitura só retornou às 10:59:01.744 — **63
segundos depois**.

Consequência direta: enquanto essa leitura não retorna, o `open_excl` do
`f_accessory` continua tomado e `openAccessory` responde `null`. O log diz
exatamente isso:

```text
[UsbRecovery] generation=2 openAccessory=failed kind=OPEN_ACCESSORY_FAILURE
  detail=Android refused to open the accessory (already open, or a stale attach)
```

A resposta do app é a rajada limitada seguida do re-probe de 30 s. Não há
give-up: um accessory que recusa agora pode abrir um minuto depois, e desistir
deixaria o painel parado com o cabo na mão.

### O teardown não pode esperar por essa leitura

`NativeUsbTransport.connect()` faz o handshake dentro de `synchronized(sessionLock)`.
Enquanto a versão anterior de `close()` também pegava esse lock, o teardown ficava
na fila atrás da leitura travada:

```text
descriptorClose=begin   10:57:59.002
descriptorClose=success 10:59:01.785   →  62,8 s
```

Sessenta e dois segundos em que o ciclo de vida não teria conseguido responder a
um replug. `close()` agora fecha o descriptor **antes de qualquer outra coisa e
sem lock nenhum** — o fechamento é a única ação com alguma chance de destravar
aquela leitura, então não pode enfileirar atrás dela. Depois do fix, no mesmo
aparelho e no mesmo cenário:

```text
descriptorClose=begin   11:03:28.142
descriptorClose=success 11:03:28.234   →  92 ms
```

### O app não é o gatilho do travamento da pilha USB — medido

**Hipótese testada e descartada em 2026-08-27.**

No primeiro ciclo físico contra a máquina de estados nova, o log mostrou o
painel reabrindo o accessory **990 ms antes** de o Android entregar
`ACTION_USB_ACCESSORY_DETACHED`:

```text
11:32:20.834  PhysicalConnectionLost
11:32:21.265  openAccessory=begin
11:32:21.312  openAccessory=success      ← em um gadget já desconectado
11:32:21.824  AccessoryDetached          ← o aviso só chega aqui
```

A hipótese era direta: abrir `/dev/usb_accessory` no meio do teardown do cabo
deixaria o `UsbDeviceManager` com a lista de funções vazia. `DETACH_GRACE_MILLIS`
foi introduzido para eliminar exatamente essa janela.

O segundo ciclo, com a grace ativa e o tablet recém-ligado
(`current_functions=ACCESSORY`, uptime 134 s), executou o caminho pretendido:

```text
11:41:44.533  PhysicalConnectionLost
11:41:44.905  descriptorClose=success
11:41:44.905  reopen=deferred in=2000ms recycles=1 grace=2000ms
11:41:45.575  AccessoryDetached          ← dentro da grace
11:41:45.575  BACKOFF -> WAITING_FOR_ACCESSORY
```

`openAccessory=begin` depois do pull: **zero**. O app não encostou no accessory.

E a pilha travou do mesmo jeito: `current_functions=[`.

Conclusão: **o travamento é causado pelo cable pull em si, dentro do
`UsbDeviceManager`, independentemente do que o app faz.** O `DETACH_GRACE_MILLIS`
continua no código porque reabrir um accessory que está sendo desmontado é
errado de qualquer forma e economiza um ciclo de descriptor — mas ele não é, e
não pode ser, a cura.

Isto também fecha uma dúvida que ficou aberta desde 2026-08-19: a afirmação de
que "só um processo novo recupera" não era sobre o app segurar alguma coisa. O
app não segura. É a pilha do aparelho que não volta.

## Instrumentação

Todo log tem o prefixo `[UsbRecovery]` e começa com a generation:

```text
[UsbRecovery] generation=12 event=AccessoryDetached state=CONNECTED
[UsbRecovery] generation=12 transition=CONNECTED->CLOSING_ACCESSORY
[UsbRecovery] generation=12 descriptorClose=begin reason=physical_detach
[UsbRecovery] generation=12 descriptorClose=success
[UsbRecovery] generation=12 transition=CLOSING_ACCESSORY->WAITING_FOR_ACCESSORY
[UsbRecovery] generation=13 openAccessory=begin attempt=1 accessory=PanelOS/PanelHost@1#33B6
[UsbRecovery] generation=13 openAccessory=success
[UsbRecovery] generation=13 plmxSession=AB12CD34
[UsbRecovery] generation=13 panelHandshake=success attachToConnectedMs=1840 recoveryMs=6210
```

Nada sensível é registrado: nem serial completo, nem caminho de dispositivo, nem
nonce completo do PLMX. O accessory aparece como
`fabricante/modelo@versão#DIGEST`, onde `DIGEST` são quatro dígitos hex de um
hash não reversível do serial — usado só para comparar, nunca para identificar.
`UsbLifecycleStateMachineTest` tem um teste que falha se um serial vazar para o
log.

### Métricas

`UsbRecoveryMetrics`, acessível pelo binder do Service:

```text
usbPhysicalDetaches          usbPhysicalAttaches
usbAccessoryOpenAttempts     usbAccessoryOpenSuccesses    usbAccessoryOpenFailures
usbDescriptorCloses          usbDescriptorCloseFailures
usbRecoveryAttempts          usbRecoverySuccesses         usbRecoveryFailures
usbHandshakeTimeouts         usbSessionSupersedes         usbStaleGenerationEventsIgnored
usbPermissionRequests        usbPermissionDenials
attachToConnectedMillis      lastRecoveryDurationMillis
```

`attachToConnectedMillis` mede do `ATTACHED` até o Panel Protocol conectado. Não
mede da inserção do cabo: o app não sabe o instante elétrico anterior ao
broadcast, e fingir que sabe tornaria o número não comparável.

## O processo não morre

`NoProcessExitTest` varre `app/src/main/java` e falha se
`Runtime.getRuntime().exit`, `System.exit`, `exitProcess`, `killProcess`,
`finishAffinity` ou `finishAndRemoveTask` reaparecerem no runtime.

Se algum uso legítimo aparecer, ele pertence a tooling ou a um teste, e precisa
ser documentado aqui antes de a lista ser afrouxada.

## Runbook da campanha física

A campanha de 5 ciclos de cabo não pode ser executada por um agente: alguém
precisa puxar o cabo. O procedimento é este.

### Pré-condição

```powershell
$adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
& $adb shell dumpsys usb | Select-String 'current_functions'
```

Se `current_functions` estiver vazio, a pilha USB do tablet está travada e
**nenhum ciclo é mensurável**: desligue e ligue o tablet (não "Reiniciar" — a
Samsung pode fazer warm boot; confirme com `adb shell cat /proc/uptime`).

Depois, com o PanelHost rodando e o Dashboard visível:

```powershell
& $adb shell pidof com.panelos.panelhome
& $adb logcat -c
& $adb logcat -s PanelOS:*
```

Anote o PID e a `generation` atual.

### Ciclo

1. Puxe o cabo fisicamente.
2. Espere 5 s.
3. Recoloque o cabo.
4. **Não** feche o app, não reinicie a Activity, não use `am force-stop` nem
   `am start`. O ADB só pode ser usado para logcat e inspeção.
5. Confirme no log, em ordem: `event=AccessoryDetached` →
   `descriptorClose=success` → `WAITING_FOR_ACCESSORY` → `event=AccessoryAttached`
   → `openAccessory=success` → `plmxSession=<novo prefixo>` →
   `panelHandshake=success`.
6. Confirme que o PID não mudou.

Um ciclo passa somente se a nova `generation` for maior que a anterior, o
`plmxSession` for diferente do anterior, e o Dashboard voltar.

### Registro

| Cycle | PID same | DETACH | Descriptor closed | WAITING | ATTACH | openAccessory | New PLMX | Panel handshake | Dashboard | Result |
| ----: | -------- | ------ | ----------------- | ------- | ------ | ------------- | -------- | --------------- | --------- | ------ |
|     1 |          |        |                   |         |        |               |          |                 |           |        |
|     2 |          |        |                   |         |        |               |          |                 |           |        |
|     3 |          |        |                   |         |        |               |          |                 |           |        |
|     4 |          |        |                   |         |        |               |          |                 |           |        |
|     5 |          |        |                   |         |        |               |          |                 |           |        |

O primeiro ciclo é gate: se ele falhar, identifique o último ponto alcançado na
lista do passo 5 antes de rodar os outros quatro.

### Testes adicionais

- **Rapid replug:** puxe e recoloque com ~1 s de intervalo, uma vez. A máquina
  precisa serializar — o log deve mostrar `attach=deferred_until_close_completes`
  e uma única abertura depois.
- **Long detach:** puxe e espere 30 s. Durante esses 30 s não pode haver nenhum
  `openAccessory=begin` no log, e a CPU do processo precisa ficar próxima de
  zero.
- **Host restart sem cable pull:** encerre e reinicie o PanelHost com o cabo no
  lugar. Esperado: `superseded=host_restart carrier=preserved`, sem
  `descriptorClose`. Este teste **não conta** para os 5 ciclos de cabo.

## Onde está o quê

| Arquivo | Papel |
| --- | --- |
| `session/UsbLifecycle.kt` | estados, eventos, ações, identidade, timeouts, métricas |
| `session/UsbLifecycleStateMachine.kt` | o redutor puro |
| `session/UsbLifecycleController.kt` | laço serializado e execução das ações |
| `session/PanelSessionService.kt` | a porta Android e o único `openAccessory` |
| `session/PanelSessionServiceRuntime.kt` | dono estável atrás do binder; executa trocas, não as decide |
| `data/transport/AccessoryHandle.kt` | dono único do descriptor |
| `data/transport/NativeUsbTransport.kt` | uma sessão física sobre um descriptor já aberto |
| `data/transport/UsbTransportEvents.kt` | classificação de falha |
