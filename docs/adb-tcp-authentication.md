# Autenticação do carrier ADB/TCP

## Threat model

ADB/TCP é um carrier suportado de desenvolvimento e fallback. As portas do
PanelHost continuam presas ao loopback, mas loopback não é autenticação. O
bootstrap descrito aqui impede que um processo local sem o token complete uma
conexão de Control, Video ou Benchmark.

O limite é deliberado: isto não é segurança geral de rede e não promete
proteção contra Administrador, `SYSTEM`, malware em kernel, inspeção da memória
dos processos ou comprometimento do próprio ADB. Não há listener em LAN e o
bootstrap não transforma ADB/TCP em um protocolo para redes não confiáveis.

## Bootstrap protocol

O bootstrap acontece no carrier, antes que o stream seja entregue ao Panel
Protocol v1, ao Panel Video Protocol v1 ou ao protocolo de benchmark. O cliente
envia um frame de prova:

| Offset | Bytes | Campo |
| ---: | ---: | --- |
| 0 | 4 | magic ASCII `PADA` |
| 4 | 1 | versão `1` |
| 5 | 1 | canal lógico: Control `1`, Video `2`, Assets/Benchmark `3` |
| 6 | 2 | tamanho do credential, unsigned big-endian |
| 8 | variável | token binário |

O tamanho declarado é limitado a 64 bytes antes de qualquer alocação e a prova
válida desta versão tem exatamente 32 bytes. O host lê com timeout de 3 segundos
e compara os 32 bytes com `CryptographicOperations.FixedTimeEquals`.

No sucesso, o host responde `PADO`, versão, canal e dois bytes zero. Só depois
desse ACK o Android expõe o socket ao protocolo superior. Magic, versão, canal,
tamanho, token, EOF, timeout ou ACK inválido encerram o socket. Cada rejeição no
host incrementa `authFailures`; nenhum valor do token entra na métrica ou no
log.

## Token generation

Cada acionamento do launcher cria 32 bytes (256 bits) com o gerador
criptograficamente seguro da plataforma: `RandomNumberGenerator` no Control
Center e nos scripts PowerShell. O token é codificado como 64 caracteres
hexadecimais apenas para atravessar ADB e o ambiente do processo.

O token é efêmero, rotacionado por execução do launcher e mantido apenas em
memória. Não deriva de serial, timestamp ou constante; não é salvo em settings,
SharedPreferences ou arquivo, não é versionado e não deve ser impresso.

Não há challenge adicional: dentro deste threat model, um segredo aleatório de
256 bits rotacionado por sessão e não observável na porta resolve a prova. Os
três sockets precisam reutilizar a credencial durante reconnects da mesma
sessão, portanto um nonce de uso único prejudicaria o lifecycle sem proteger
contra Administrador ou malware capaz de inspecionar processos.

## Token distribution

No PC, o launcher mantém o token em memória e entrega-o ao processo filho
PanelHost pela variável de ambiente `PANELOS_ADB_AUTH_TOKEN`. O valor não aparece
nos argumentos do PanelHost. O host exige exatamente 32 bytes hexadecimais ao
selecionar `--transport adb` e falha antes de abrir listeners quando a variável
está ausente ou inválida.

No Android, o launcher usa o fluxo ADB de desenvolvimento para iniciar
`com.panelos.panelhome/.MainActivity` com o extra em memória
`panelAdbAuthToken`. `am start -S` substitui o processo/sessão anterior. O app
decodifica o extra uma vez ao construir `AdbTcpTransport`; um início comum sem o
extra não consegue abrir ADB/TCP autenticado.

Mensagens de erro do launcher passam por redação do token. Os scripts não
imprimem o valor e removem a variável do ambiente ao terminar.

## Control protection

`127.0.0.1:8765` exige o bootstrap com canal `1`. O `TcpPanelConnectionListener`
só cria o `IPanelConnection` depois da validação e do ACK. Portanto o leitor
JSONL recebe exatamente o primeiro byte do Panel Protocol v1, sem campo novo de
autenticação e sem alteração de fixture.

## Video protection

`127.0.0.1:8766` exige o mesmo token com canal `2`. O hello binário `PNLV` só é
lido depois do bootstrap. Uma prova válida de Control não pode ser reutilizada
como frame de Video porque o canal faz parte do frame validado.

## Benchmark protection

Quando `--transport-benchmark` expõe `127.0.0.1:8767`, o canal Assets `3` passa
pelo mesmo listener autenticado. `run-transport-benchmark.ps1` cria uma sessão
nova, configura os três reverses e entrega o mesmo token ao app e ao host. A
porta 8767 não é aberta pelo fluxo normal sem benchmark.

## Native USB unchanged

Native USB não lê `PANELOS_ADB_AUTH_TOKEN`, não recebe o extra Android e não
executa `PADA`/`PADO`. Ele continua usando PLMX v2. Pairing/autenticação de USB
nativo (SEC-001) é uma frente separada e futura.

## Tests

Os testes do host cobrem token válido em Control, Video e Assets/Benchmark,
token diferente, mensagem ausente, credential oversized, timeout e canal
divergente. Também verificam fechamento antes de expor a conexão e incremento
de `authFailures`.

Os testes Android exercitam o bootstrap no socket real de Video antes do hello.
Os testes do Control Center verificam token de 256 bits, argumentos ADB
estruturados, distribuição no ambiente do PanelHost e redação.

## Build

Validação local do conjunto:

```powershell
.\gradlew.bat testDebugUnitTest
.\gradlew.bat assembleDebug
dotnet test windows\PanelHost.Tests\PanelHost.Tests.csproj
dotnet test windows\PanelOS.ControlCenter.Tests\PanelOS.ControlCenter.Tests.csproj
dotnet build windows\PanelOS.sln
.\gradlew.bat lintDebug
```

## Development workflow

Pelo script:

```powershell
.\scripts\start-panel-dev.ps1
```

Ou, no Control Center, habilite **Modo de desenvolvimento** e pressione
**Iniciar desenvolvimento**. Nos dois casos, uma única ação:

1. cria e rotaciona o token;
2. configura e confirma `adb reverse` para 8765 e 8766;
3. inicia um processo novo do PanelHome com o token;
4. inicia ou reinicia o PanelHost com o mesmo token.

Não existe etapa de copiar ou colar segredo. Benchmarks usam o launcher próprio,
que inclui 8767 quando o carrier selecionado é ADB. Executar PanelHost diretamente
com `--transport adb` sem o ambiente preparado falha de propósito.
