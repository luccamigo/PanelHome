# PanelOS Control Center

Aplicação Windows (.NET 8 + WPF) para administrar o PanelOS no PC. Ela mostra o
estado do `PanelDisplayDriver`, conecta e desconecta o monitor virtual, edita a
configuração do Stream Deck e reúne informações de diagnóstico.

O `PanelDisplayDriver` continua sendo apenas um Indirect Display Driver: ele
cuida do display virtual, do swapchain, dos frames e do IddCx. Nenhuma interface
gráfica vive dentro do driver.

```text
PanelOS Control Center
        │
        ├── Driver: status, connect/disconnect
        ├── PanelHost: iniciar, parar, reiniciar, logs
        ├── Stream Deck: configuração e execução
        ├── Mídia em tela cheia: GIF ou MP4 em loop no tablet
        └── Configurações
```

## Mídia em tela cheia

A seção **Mídia em tela cheia** permite escolher ou remover uma única mídia
GIF/MP4 de até 64 MiB. Ela exibe nome, formato e tamanho; erros de leitura,
formato ou limite aparecem junto às ações e preservam a mídia anterior.
Os botões ficam indisponíveis enquanto a operação está em andamento.

O tablet reproduz sem áudio, com proporção preservada, na última página do
carrossel. As páginas da fase1 continuam na ordem existente. A reprodução pausa
fora da página ou com a Activity fora de primeiro plano. Sem negociação de
`loop_media` nos dois lados, não há pedido de download. O contrato, cache e
validação estão em [docs/loop-media.md](../../docs/loop-media.md).

## Gerenciamento do PanelHost

O Control Center inicia o agente diretamente por `ProcessStartInfo`. Ele **não**
executa `cmd.exe`, PowerShell nem `scripts/start-panel-dev.ps1`: aquele script
continua sendo o atalho de desenvolvimento, e a aplicação não depende dele.

`IPanelHostProcessManager` expõe `Start`, `Stop`, `Restart` e `Refresh`, com
estado explícito em vez de um booleano:

```text
Stopped → Starting → Running → Stopping → Stopped
                        ↓
                      Error
```

O status carrega PID, início, caminho do executável e — o que organiza todo o
resto — **de quem é o processo**:

| Situação                              | O que o Control Center faz                 |
| ------------------------------------- | ------------------------------------------ |
| agente iniciado por ele                | mostra, captura a saída, pode parar e reiniciar |
| agente iniciado por fora               | mostra `Iniciado externamente`; não para, não reinicia, não mata ao sair |
| já existe um agente                    | recusa iniciar um segundo (`already_running`) |
| executável não encontrado              | `executable_not_found`, com o caminho procurado |
| agente encerrou sozinho                | `Error` com código de saída e duração       |

Um processo que não é nosso nunca é encerrado — nem pelo botão Parar, nem ao
fechar o Control Center.

### Onde o PanelHost.exe é procurado

`IPanelHostExecutableLocator` resolve, nesta ordem: o caminho configurado em
Configurações; `PanelHost.exe` ao lado do Control Center (o layout distribuído);
e, em desenvolvimento, a build mais recente sob `windows/PanelHost/bin`,
encontrada subindo a partir do assembly em execução. Nenhum caminho absoluto
desta máquina fica no código.

### Encerramento

O agente é iniciado sem console e com os pipes ligados, então não há Ctrl+C para
enviar nem janela para fechar: parar significa encerrar o processo que o Control
Center iniciou, junto com o que ele tiver criado. Se o monitor virtual estiver
conectado nesse momento, ele continua conectado — desconecte pelo Dashboard.

### Logs

A saída do agente que o Control Center iniciou vai para um buffer circular de
1000 linhas (a aba Diagnóstico mostra, copia e limpa a visualização) e, se a
opção estiver ativa, para arquivos rotativos em
`%LOCALAPPDATA%\PanelOS\logs\` — no máximo 5 arquivos de 5 MB, ou seja, 25 MB no
teto. Um agente iniciado por fora mantém o próprio console e não aparece ali.

## Transporte de desenvolvimento (ADB)

Hoje o tablet alcança o PanelHost por `adb reverse`: o app conecta em
`127.0.0.1` e o cabo carrega as portas 8765 (controle) e 8766 (vídeo). A ação
**Iniciar desenvolvimento** cria um token efêmero de 256 bits, configura os
reverses, inicia o app pelo ADB e inicia/reinicia PanelHost com o mesmo token.
Cada socket é autenticado antes de chegar aos protocolos. **Isso é um carrier de
desenvolvimento/fallback, não segurança geral de rede.**

Por isso o ADB vive em `PanelOS.Windows.Development`, atrás de
`IAdbDevelopmentTransport`, e só aparece na interface com **Modo de
desenvolvimento** ligado. Nada do funcionamento normal do Control Center depende
dele, e quando o transporte USB definitivo existir, essa interface é removida
sem que nenhum outro componente mude.

O `adb.exe` é procurado no caminho configurado, em
`%LOCALAPPDATA%\Android\Sdk\platform-tools`, em `ANDROID_SDK_ROOT`/`ANDROID_HOME`
e no `PATH`. Ele é executado diretamente, com argumentos estruturados — nunca uma
linha de comando montada para um shell interpretar.

O token permanece em memória, chega ao Android como extra do intent ADB e ao
PanelHost pelo ambiente do processo filho. Ele não é salvo nem exibido nos
logs. Veja o threat model e o framing em
[`docs/adb-tcp-authentication.md`](../../docs/adb-tcp-authentication.md).

## Projetos

```text
windows/PanelOS.Windows            biblioteca compartilhada
windows/PanelOS.ControlCenter      aplicação WPF
windows/PanelOS.ControlCenter.Tests
```

`PanelOS.Windows` reúne o que não é interface gráfica:

- `Driver/` — fachada pública (`IPanelDriverController`) sobre o cliente do
  assembly compartilhado `PanelOS.DriverInterop`;
- `StreamDeck/` — domínio, validação, serialização, persistência e DTO;
- `PanelOSPaths` — diretórios por usuário.

### Reúso do interop, sem duplicação

O P/Invoke do `PanelDisplayDriver` **não** é copiado nem compilado em mais de
um assembly. `PanelOS.Windows` e `PanelHost` referenciam o projeto inferior
compartilhado:

```xml
<ProjectReference Include="..\PanelOS.DriverInterop\PanelOS.DriverInterop.csproj" />
```

`PanelDisplayDriverClient`, `PanelDisplayDriverContract`, os modelos gerenciados
e o P/Invoke pertencem exclusivamente ao assembly `PanelOS.DriverInterop`, no
namespace de mesmo nome. `driver/PanelDisplayDriver/driver/Public.h` permanece
a fonte de verdade do Driver Contract v5.

A superfície pública de aplicação consumida pela interface continua em
`PanelOS.Windows.Driver`:

- `PanelDriverSnapshot` — disponibilidade, ciclo de vida, monitor, modo ativo,
  modos anunciados, versão de protocolo e caminho da interface;
- `PanelDriverCommandResult` — resultado de CONNECT/DISCONNECT.

## Driver

O Control Center fala diretamente com a device interface, exatamente como o
PanelHost: ele localiza a interface pelo GUID, abre um handle por operação e
envia `GET_STATUS`, `CONNECT_DISPLAY` e `DISCONNECT_DISPLAY`. Ele **não** executa
`PanelDisplayCtl.exe` como subprocesso e não instala nem remove o driver.

Falhas são dados, não exceções: sem driver instalado a janela abre normalmente e
mostra `PanelDisplayDriver não encontrado`. Acesso negado é distinto de driver
ausente, porque as ações do usuário são diferentes.

`PanelDisplayDriverController` é um plano de controle fino. Ele não reimplementa
o ciclo de vida do Desktop do PanelHost: os dois comandos são idempotentes no
contrato do driver, e o driver continua sendo a fonte de verdade sobre a
existência do monitor.

## Stream Deck

### Domínio

```text
StreamDeckConfiguration  version, activeProfileId, profiles[]
StreamDeckProfile        id, name, pages[]
StreamDeckPage           id, name, rows, columns, buttons[]
StreamDeckButton         id, row, column, label, icon, action
```

O domínio não depende de WPF. O Control Center o edita hoje; o PanelHost vai lê-lo
para resolver um botão invocado.

### Ações

`PanelAction` é tipada, não uma string de comando:

```text
LaunchApplication   executablePath, arguments, workingDirectory
OpenUrl             url
KeyboardShortcut    modificadores estruturados + tecla
MediaCommand        PlayPause, Previous, Next
VolumeCommand       MuteToggle, VolumeUp, VolumeDown, SetLevel (level reservado)
PanelModeCommand    Dashboard, Desktop, Ambient, StreamDeck
```

Novos tipos (`RunScript`, `RunCommand`, `PowerShell`) entram como uma classe
selada nova mais um ramo no serializador. Eles **não** existem hoje, e não há
nenhum campo na interface onde se digite um comando ou um script.

Uma ação incompleta é um estado normal de edição, não um erro. O profile padrão
já funciona sem configuração — mídia, volume, `Win+D` e captura de tela — e a
última linha traz espaços reservados para aplicativos, sem caminho preenchido,
porque inventar um executável produziria um botão que falha.

### Runtime das ações

`WindowsStreamDeckActionExecutor` executa um botão de verdade neste PC:

```text
ExecuteAsync(buttonId)   →  resolve o id na configuração local e executa
ExecuteButtonAsync(botão) →  executa um botão já resolvido (usado pelo "Testar ação")
```

O resultado é sempre um `StreamDeckActionResult` com `Success`, `ErrorCode` e
`Message` — exceção não é fluxo normal. Os códigos são estáveis porque também
serão a resposta de um futuro `streamdeck.invoke`:

| Código                     | Quando                                            |
| -------------------------- | ------------------------------------------------- |
| `button_not_found`         | o id não existe na configuração do PC              |
| `button_disabled`          | o botão existe mas está desativado                 |
| `action_not_configured`    | sem ação, sem executável, sem URL ou sem tecla     |
| `application_not_found`    | o caminho absoluto configurado não existe          |
| `invalid_url`              | URL malformada ou fora de http/https/mailto        |
| `shell_not_allowed`        | script/atalho, ou interpretador com argumentos     |
| `unsupported_action`       | modo do painel, volume absoluto, ação desconhecida |
| `execution_failed`         | o Windows recusou a operação                       |
| `configuration_unavailable`| a configuração não pôde ser lida                   |

Como cada ação é executada:

- **LaunchApplication** — `ProcessStartInfo` com `UseShellExecute = false`, ou
  seja, `CreateProcess` direto. Nunca passa por `cmd.exe` nem por PowerShell, e
  a linha de comando não é montada concatenando o executável com texto do
  usuário: os argumentos configurados vão inteiros para `Arguments`. Sem
  diretório de trabalho configurado, o padrão é a pasta do próprio aplicativo.
- **OpenUrl** — `UseShellExecute = true`, deixando o Windows escolher o
  navegador ou o cliente de e-mail padrão. Só `http`, `https` e `mailto` são
  aceitos; `file:`, `javascript:`, `ms-settings:` e afins são recusados.
- **KeyboardShortcut**, **MediaCommand**, **VolumeCommand** — `SendInput` via
  Win32, não `SendKeys`: um atalho global como `Win+D` precisa de eventos reais
  de virtual-key na fila do sistema, não de texto enviado à janela em foco.
- **PanelModeCommand** — responde `unsupported_action`. Ele depende da
  integração com o PanelHost, que não existe; simular sucesso seria mentir.

Duas regras impedem que "abrir um aplicativo" vire um shell:

1. só arquivos `.exe` são iniciados — `.bat`, `.cmd`, `.ps1`, `.vbs`, `.js`,
   `.lnk` e `.msi` são recusados com `shell_not_allowed`;
2. um interpretador conhecido (`cmd`, `powershell`, `pwsh`, `wscript`,
   `cscript`, `mshta`, `rundll32`, `regsvr32`) pode ser aberto sozinho, mas
   nunca com argumentos — isso seria execução de comando arbitrário com outro
   nome.

O atalho é montado por `KeyboardChordBuilder`, que é puro e testável:
`Ctrl+Shift+S` vira exatamente `ctrl↓ shift↓ s↓ s↑ shift↑ ctrl↑`, e o serviço de
entrada solta as teclas que chegou a pressionar mesmo se o lote falhar no meio.

O log fica em `[StreamDeck] Invoke button=… / Action=… / Success` e
`[StreamDeck] Failed code=…`. Ele não registra caminho, argumentos nem URL: uma
query string pode carregar token e uma linha de argumentos pode carregar
credencial.

### Ícones

Um ícone próprio nunca vive dentro do JSON nem como caminho de arquivo. O
Control Center importa a imagem — escolhida pelo usuário ou extraída do próprio
`.exe` com `PrivateExtractIcons` — normaliza para PNG 128×128 com transparência
e grava no cache:

```text
%LOCALAPPDATA%\PanelOS\icons\<iconId>.png
```

O `iconId` vem do conteúdo (hash), então dois botões com a mesma imagem
compartilham um arquivo. A configuração guarda `iconId` (e, apenas para exibir a
origem, `sourcePath`); o DTO do tablet leva **somente** o `iconId`.

### Persistência e schema

```text
%LOCALAPPDATA%\PanelOS\streamdeck.json
```

O documento declara sempre a versão do schema:

```json
{ "version": 2 }
```

O schema v2 substituiu o caminho do ícone por um `iconId` e acrescentou
`enabled` por botão. Um documento v1 é reescrito na leitura por
`StreamDeckSchemaMigrations`, que trabalha sobre a árvore JSON — assim o modelo
só conhece a forma atual e um campo aposentado não precisa sobreviver nele para
sempre. O ícone v1 vira um ícone próprio com a origem preservada, pronto para
ser reimportado.

`JsonStreamDeckConfigurationStore` nunca falha ao carregar:

| Situação no disco            | Resultado                                              |
| ---------------------------- | ------------------------------------------------------ |
| arquivo ausente              | profile padrão, gravação liberada                       |
| JSON válido e consistente    | configuração carregada                                  |
| JSON corrompido ou inválido  | profile padrão; o arquivo original é movido para `.invalid-<data>.json` |
| `version` mais novo          | profile padrão, **gravação bloqueada** na interface     |
| erro de I/O                  | profile padrão, gravação bloqueada                      |

Um arquivo escrito por uma versão futura nunca é sobrescrito por esta. Ações de
tipo desconhecido são preservadas na íntegra ao regravar, para que um downgrade
não destrua a configuração de quem usa uma versão mais nova.

A gravação é atômica (arquivo temporário seguido de troca) e é validada antes:
ids de página e de botão são únicos globalmente, cada botão fica dentro do grid,
duas posições não colidem, um `iconId` tem forma válida e o grid respeita 2–6
linhas por 2–6 colunas.

Reduzir o grid nunca apaga um botão: se algum ficaria fora do novo tamanho, a
mudança é recusada e o editor pede para movê-lo ou removê-lo antes.

Ids de botão precisam ser globalmente únicos porque o tablet endereça um botão
apenas pelo id — um id repetido tornaria a invocação ambígua.

## Modelo de segurança

O tablet **nunca** envia algo executável. O que atravessa o cabo é um
identificador:

```text
Tablet
→ streamdeck.invoke(buttonId)
→ PanelHost
→ busca a configuração local do PC
→ executa a ação previamente configurada
```

A alternativa rejeitada é o tablet mandar uma linha de comando, um script ou uma
string de shell para o PC executar:

```text
Tablet
→ powershell.exe <texto arbitrário>
```

Isso transformaria o transporte USB em execução remota de código no desktop.
Por isso `executablePath`, `arguments`, `workingDirectory`, `url` e o caminho de
origem de um ícone permanecem no PC.

`IStreamDeckActionExecutor.ExecuteAsync` tem exatamente essa forma: recebe um
`buttonId` e nada mais. Não existe sobrecarga que aceite caminho, argumento ou
comando vindo de fora — a única outra entrada, `ExecuteButtonAsync`, recebe um
objeto de domínio que o próprio PC acabou de resolver, e é o que o botão
"Testar ação" usa para experimentar uma edição ainda não salva.

O runtime já executa ações de verdade, então essa fronteira deixou de ser
teórica: os testes em `StreamDeckSecurityTests` falham se qualquer ação iniciar
um interpretador, se um script puder ser configurado como aplicativo, ou se uma
ação de tipo desconhecido chegar a executar.

### DTO enviado ao tablet

`StreamDeckLayoutDto` carrega somente identidade e metadados visuais:

```text
page id, page name, rows, columns
button id, row, column, label, icon, enabled
```

Não existe campo para caminho de executável, linha de comando, script ou URL.
`StreamDeckLayoutProjector` é o único lugar que faz essa projeção, e o teste
`StreamDeckLayoutDtoTests` falha se qualquer configuração do PC vazar para o
JSON — inclusive por um membro novo com nome suspeito.

Um ícone próprio vira uma referência opaca (`kind: image`, `iconId`), nunca o
caminho: uma mensagem futura do protocolo poderá buscar os bytes por id. Um
ícone ainda não importado é enviado como `none`, e não como a origem no disco.

## Perfil ativo e revisão

Somente um perfil vai ao tablet por vez. `activeProfileId` no documento diz
qual, e **selecionar** um perfil no editor não é o mesmo que **publicá-lo**:
navegar entre perfis não muda o que está na mesa. O botão **Usar no tablet**
grava a escolha imediatamente pela store existente, e excluir o perfil ativo
move a marca para o primeiro restante em vez de deixá-la apontando para nada.

`StreamDeckLayoutRevision` nomeia uma configuração publicada. É um digest do
documento canônico inteiro, e não da projeção enviada ao tablet, porque
repontar um botão para outro aplicativo não muda nada que o tablet possa ver —
e é exatamente o caso que a verificação de revisão existe para pegar. O valor é
um hash: nada da configuração pode ser lido de volta a partir dele.

## Integração com o PanelHost

O Stream Deck trafega pelo cabo. `PanelHost` referencia `PanelOS.Windows` e
reutiliza `IStreamDeckConfigurationStore`, `WindowsStreamDeckActionExecutor`,
`StreamDeckLayoutDto` e o cache de ícones; nada disso é duplicado no agente.

```text
Control Center → streamdeck.json → PanelHost (watcher + debounce)
                                        ↓ streamdeck.layout
                                    PanelHome
                                        ↓ streamdeck.invoke (buttonId, revision)
                                    PanelHost
                                        ↓
                              WindowsStreamDeckActionExecutor
```

1. o PanelHost lê a configuração pela store — em modo somente leitura, sem
   colocar de quarentena o arquivo que o usuário está editando do outro lado;
2. observa `%LOCALAPPDATA%\PanelOS\streamdeck.json` com `FileSystemWatcher`,
   agrupa a rajada de eventos de uma gravação atômica com um debounce curto e
   relê uma vez só;
3. envia `streamdeck.layout` logo após o handshake e a cada mudança válida;
4. recebe `streamdeck.invoke` com `buttonId` e `revision`;
5. resolve o id **no snapshot cuja revisão o tablet devolveu** e chama
   `ExecuteButtonAsync`, o que fecha a janela entre "a revisão confere" e
   "execute";
6. responde `streamdeck.invoke_result` com o `ErrorCode` da tabela acima e um
   texto escolhido de uma tabela fixa — a mensagem do executor nunca é
   encaminhada, porque pode citar um caminho.

Uma revisão antiga não executa nada: responde `stale_layout` e manda o layout
atual na sequência. Um documento inválido ou pego no meio da troca atômica é
relido algumas vezes e, se continuar assim, o último layout bom permanece
publicado em vez de a configuração do usuário desaparecer da tela.

Os bytes de um ícone viajam pelo canal de controle em Base64, apenas quando o
tablet pede um `iconId` que ainda não tem em cache. O id é validado antes de
virar caminho, só ids que o layout publicado usa são servidos e um arquivo
acima do limite responde `available: false`.

## Interface

- **Dashboard** — cartões medidos. Driver e Display vêm do
  `IOCTL_PANEL_GET_STATUS`. PanelHost mostra estado, PID, de quem é o processo e
  traz Iniciar, Reiniciar e Parar. Stream Deck mostra o perfil ativo, quantas
  páginas e quantas ações configuradas ele tem, e a revisão publicada — números
  contados da configuração salva, nunca estimados. Tablet é um cartão separado
  que diz `Indisponível`: um agente em execução **não** significa tablet
  conectado, e descobrir isso exigiria falar o protocolo de controle.
- **Stream Deck** — três áreas: navegação, preview do tablet e editor do botão.

  ```text
  ┌────────────┬────────────────────────┬─────────────────┐
  │ Perfis     │  ‹ Principal · Mídia › │ Aparência       │
  │ Principal  │  ┌──────────────────┐  │  Nome, ícone    │
  │ Mídia      │  │ [VS][Spotify][ ] │  │ Ação            │
  │ Dev        │  │ [Mute][Play ][ ] │  │  Tipo, campos   │
  │            │  └──────────────────┘  │                 │
  │ Usar no    │  Adicionar Remover ←→  │ [Testar ação]   │
  │ tablet     │                        │                 │
  └────────────┴────────────────────────┴─────────────────┘
  ```

  O preview usa a proporção do painel e mostra exatamente o que o tablet
  desenha: linhas, colunas, ícones, rótulos e células vazias. As páginas viram
  abas acima dele, e o cabeçalho diz qual perfil está no tablet.

  Perfis e páginas continuam com novo, renomear, duplicar e excluir — a exclusão
  pede confirmação e nunca deixa a configuração sem perfil ou sem página. O grid
  vai de 2×2 a 6×6 e a reordenação é por mover para a esquerda, direita, cima e
  baixo: mover troca o conteúdo das duas células, e não há drag and drop.

  O editor agrupa **Aparência** (nome, ícone, habilitado) e **Ação** (tipo e só
  os campos daquele tipo: executável, argumentos e diretório para um aplicativo;
  URL para um link; modificadores e tecla para um atalho; um dropdown para mídia
  ou volume). **Testar ação** executa o botão selecionado neste PC e responde ali
  mesmo — `Executado`, `Aplicativo não encontrado`, `URL inválida`, `Ação ainda
  não configurada` — sem caixa de diálogo.
- **Diagnóstico** — interface e GUID do driver, protocolo informado e esperado,
  ciclo de vida, monitor, modo ativo, modos anunciados, estado e executável do
  PanelHost, código da última saída, caminhos de configuração e de logs e versão
  do Control Center. Abaixo, a saída capturada do agente, com Copiar e Limpar
  visualização. Os cartões de Capture FPS, Encode latency, USB bandwidth e
  Tablet render FPS existem como espaço reservado e permanecem vazios: nenhum
  número é estimado.
- **Configurações** — iniciar o Control Center com o Windows (chave `Run` do
  usuário atual, sem exigir administrador); iniciar o PanelHost junto com o
  Control Center; encerrar o agente ao sair; reiniciá-lo após uma queda; e
  minimizar para a bandeja. Quando um controle não pode funcionar nesta máquina,
  ele aparece desabilitado com o motivo em vez de fingir que funciona.

O ícone de bandeja oferece Abrir PanelOS, Iniciar/Reiniciar/Parar PanelHost,
Conectar e Desconectar display e Sair, com cada item habilitado conforme o
estado — parar e reiniciar ficam indisponíveis para um agente externo. Ele usa o
ícone do próprio executável, então a aplicação não carrega nenhum asset binário.

### Início automático e saída

`Iniciar o PanelHost com o Control Center` verifica antes de agir: se já existe
um agente, nenhum segundo é iniciado. Ao sair, o padrão é **manter o PanelHost
em execução** — fechar a janela não precisa derrubar o painel. Com
`Encerrar o PanelHost ao sair` ligado, só o agente que o Control Center iniciou é
encerrado.

O reinício após queda é limitado a 3 tentativas em 60 segundos; passando disso o
estado vira `Error` com "Reinício automático pausado", em vez de um laço de
reinícios que esconde a causa.

A entrada de logon separada do PanelHost, que existia antes, é removida na
primeira execução: com o Control Center gerenciando o agente, uma segunda
entrada produziria um processo que ele não controla.

## Escopo não incluído

- instalar ou remover o driver pela interface;
- executar scripts, comandos ou PowerShell a partir de um botão;
- aplicar um modo do painel (depende do PanelHost);
- definir um volume absoluto;
- escolher o perfil pelo tablet, editar botões pelo tablet ou arrastá-los lá;
- pastas aninhadas, long press, multitouch no Stream Deck;
- métricas de captura, encode e transporte;
- qualquer alteração no driver ou no pipeline de vídeo.

## Build e testes

```powershell
dotnet build windows/PanelOS.Windows/PanelOS.Windows.csproj
dotnet build windows/PanelOS.ControlCenter/PanelOS.ControlCenter.csproj
dotnet test windows/PanelOS.ControlCenter.Tests/PanelOS.ControlCenter.Tests.csproj
dotnet run --project windows/PanelOS.ControlCenter
```
