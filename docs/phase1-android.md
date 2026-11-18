# Android — B1 (PAN-15 e PAN-14 somente leitura)

Base: versão anterior do projeto. Implementação Compose no toolkit
existente, preservando tema e identidade definidos em `DESIGN-fase1.md`.

## Superfície

- `computer_stats` e `processes` são features aditivas do hello. Sem negociação,
  as páginas novas não existem e nenhum `processes.options` é enviado.
- Carrossel: Painel, todas as páginas existentes do Stream Deck, Computador,
  Processos. Toque no indicador abre a lista de páginas; salto direto.
- Preferências locais: páginas ligadas por padrão, janela de 5 min (1/5/15),
  ordenação CPU/RAM e agrupamento desligado. Só essas preferências vão a disco.
- CPU/RAM/GPU usam a telemetria existente; rede usa o contrato `network_rates`.
  Histórico de até 15 min no serviço, independente da página/Activity; desaparece
  ao encerrar a sessão do serviço/processo. Não há exportação ou histórico em disco.
  A troca de sessão de transporte por retirada física do cabo conserva o histórico
  na memória do runtime e o entrega ao coordenador novo, fechando a lacuna na reconexão.
- Valores ausentes interrompem o traço. Desconexão mantém histórico apagado e
  valores atuais indisponíveis. Reconexão mostra o intervalo listrado, sem interpolar.
  Relógio local monotônico `elapsedRealtime` no Android; timestamps do PC não
  definem o eixo. Troca de nome do host limpa histórico para não misturar PCs.
- Canvas sem animação dos dados, com envelope mínimo/máximo por pixel; rede
  espelhada com mesma escala 1/2/5 em ambas as direções. Detalhe bloqueia pager,
  permite consultar hora/valor e mostra média/mínimo/máximo e extras existentes.
- Processos: seleção enviada ao host, top15 conforme resposta; números continuam
  mudando durante o toque. Ordem congelada enquanto toca/rola e por 2 s depois;
  nova posição exige duas amostras consecutivas. PID + início identificam a linha;
  processo encerrado durante o gesto mantém o espaço e indica término até liberar.
- Linhas expandem para identidade, tempo aberto e motivo de somente leitura.
  Não existe comando/botão de encerrar nesta entrega B1.
- Amostra nova inválida negociada vira lacuna/erro, sem zerar e sem derrubar o
  transporte saudável. Erros de envelope/handshake mantêm a política existente.
- Redução de movimento consulta/observa `ANIMATOR_DURATION_SCALE`; sem movimento
  espacial nesse modo. Descrições dos tiles atualizam no máximo a cada 10 s,
  exceto mudança explícita de janela/conexão. Números usam `tnum`, uma linha e
  tamanho menor quando fonte ampliada.

## Validação B1 (16/11/2026)

O mantenedor liberou uma janela exclusiva. Execução sequencial com `--no-parallel
--max-workers=1` e `ANDROID_HOME=<caminho-do-Android-SDK>`:

- JVM: 456 casos, 452 executados, 4 ignorados por precondições da base,
  zero falhas/erros. Excluídas, por instrução do mantenedor, `TcpVideoConnectionTest`
  e `CrossLanguageLiveSessionTest` via init script local fora do repositório.
- `lintDebug`: zero erros, 13 avisos. Sem mudança de baseline/suppressions.
- `assembleDebug`: sucesso; APK somente nesta worktree, 28.246.555 bytes.
- `git diff --check`: sem achados.

Adendos posteriores à janela: encaminhamento dos controles pelo runtime e
retenção em retirada física do cabo, com testes de regressão do runtime escritos.
Esses adendos aguardam a próxima janela exclusiva; os números acima são da
validação de uma versão anterior, não certificam os commits posteriores.

Não houve ciclo vermelho→verde: testes foram escritos durante a proibição de
execução e rodados somente após a liberação. Nenhum host real/emulador/tablet
foi iniciado ou conectado. Não há evidência visual ou de desempenho em aparelho.

Testes adicionados e passando para contrato/nulls/limites, host antigo, retenção/gaps,
ordem durante toque, duas amostras, PID reutilizado, seleção, envelope dos picos,
estatísticas e aritmética do carrossel. Comandos (as exclusões JVM acima precisam
ser aplicadas por init script na validação autorizada):

```powershell
.\gradlew.bat testDebugUnitTest
.\gradlew.bat lintDebug
.\gradlew.bat assembleDebug
```

A saída padrão fica exclusivamente em `app/build/outputs/apk/debug/` nesta
worktree. Nunca copiar sobre o APK API28 congelado de uma branch anterior.

Pendências de aparelho (janela posterior de teste de UX): capturas 1280×800,
fonte 1,3×, TalkBack, remover animações, gestos e ≥30 fps por 5 min com histórico
cheio. Custo extra ≤1% no PC e normalização de CPU precisam de medição do host;
a soma do top15 não garante soma de todos os processos ±5 pontos.

Limites da implementação: B1 não traz ícones de processos no protocolo (usa
inicial genérica); não há PAN-9/PAN-6 nem encerramento de processos neste lote.
Não foram usados LAN/ADB/tablet/firewall/settings reais nem o host/PID1192.

## Validação B2/B3 (16/11/2026, segunda janela exclusiva)

Mesmos comandos sequenciais e exclusões da primeira janela, sobre o código com PAN-9, PAN-6 e B3:

- JVM: 474 casos, 4 ignorados (precondições da base), zero falhas. Inclui 11 casos do controlador de
  encerramento e 5 do protocolo `processes.terminate`.
- `lintDebug`: zero erros, 13 avisos (mesmo número da primeira janela). `assembleDebug`: sucesso.
- Control Center: `dotnet test PanelOS.ControlCenter.Tests`, 341 aprovados, 0 falhas.
- Correção na janela: colisão de assinatura JVM em `FakeServiceSession` e `using System.IO` no teste WPF.

## B3: encerrar processo pelo tablet (gate desligado nesta entrega)

- Comando `processes.terminate {requestId, pid, startTimeUtcTicks}`; resposta `processes.terminate_result`
  com `disabled|invalid_request|gone|denied|protected|terminated`. O tablet nunca envia caminho.
- O botão só existe com `terminateEnabled=true` no snapshot, lista não agrupada, processo legível, do usuário
  atual e não protegido. O host numa versão anterior envia `terminateEnabled=false` enquanto `Reviewed=false`,
  então nada aparece nem é enviado nesta rodada; o texto explica que está desligado no Control Center.
- Segurar 1,2 s (linear); soltar ou andar mais de 8 dp cancela; um pedido por vez; sem resposta em 5 s ou sem
  conexão, o texto diz que nada foi confirmado e nunca afirma sucesso.
- Limitação: o gesto de segurar não foi exercitado em aparelho nem com TalkBack (toque duplo e segurar deve
  funcionar, não verificado). Os textos e a lógica estão cobertos por testes JVM; o botão Compose não tem teste de UI.

## Revisão do contrato 01d2ba3 (17/11/2026)

- PAN-6 lê `contentHidden` aditivo (ausente = false, presente exige booleano) e preserva
  `omitted`. Mostra junto às decisões "Texto cortado · N caracteres omitidos" e
  "O conteúdo da edição não está visível neste resumo". Somente o resumo rola;
  Negar, Permitir e Deixar no PC continuam abaixo dos avisos. Resumo incompleto
  não desabilita Permitir; a trava de 700 ms, expiração e conexão continuam valendo.
- PAN-14 respeita a proteção oferecida pelo host, inclusive OpenBot e cua-driver,
  sem botão para esses processos. O gate permanece desligado; não foi encerrado
  processo algum. O controlador cobre envio negado a processo protegido.
- Control Center usa somente `PanelOS.Windows/Management/ClaudeHookInstaller.cs`,
  `ClaudeHookProtocol.cs` e `LocalIntegrationAdmin.cs`, idênticos aos arquivos de
  branch de tarefa; não há outro instalador no WPF. A prévia
  mostra pasta estável, quatro arquivos com SHA-256/tamanho e JSON selecionável.
  Estados de arquivo alterado/ausente e versão desatualizada orientam reinstalação.
- Validação sequencial: JVM 480 casos, 476 executados, 4 ignorados, zero falhas;
  mantidas as exclusões de `TcpVideoConnectionTest` e `CrossLanguageLiveSessionTest`
  por init script externo. `lintDebug`: zero erros, 13 avisos. `assembleDebug` e
  `assembleDebugAndroidTest`: sucesso. Control Center: 344/344 testes aprovados.
  O teste inicial de status não compilou por acessar método internal; foi corrigido
  para conferir `HookStatus` público após mudanças somente em fixtures TEMP.
- `Phase1ScreenshotsTest` renderiza as telas Compose reais com a sessão falsa já
  usada nos smoke tests, sem host/rede/processos reais. Produz imagens de avisos,
  banner, pedidos Claude e processos em `getExternalFilesDir/phase1-screenshots`.
  Não é uma tela nem um modo de demonstração de produção. Execução e imagens
  do emulador são registradas na entrega compartilhada, separadas dos testes JVM.
- Não houve instalação real do hook, escrita em `~/.claude` real, push, merge,
  deploy, LAN, tablet físico ou alteração do APK API28 congelado. Leitor de tela,
  desempenho físico e uso real do Claude permanecem fora desta validação.

### Pós-revisão de segurança H1–H4

- H1 reproduzido no emulador com 360 caracteres sem espaços e fonte 1,3×:
  `hasVisualOverflow=true`, seis linhas, caixa 718×244 px. O resumo agora não tem
  limite de linhas nem elipse visual em `head`/`tail`; só o trecho central que o
  host informa em `omitted` fica ausente. A coluna permite rolar até o fim, sem
  tirar Negar, Permitir e Deixar no PC da tela.
- Regressão Compose usa a dependência oficial `ui-test-junit4`, somente em
  androidTest e pela mesma BOM do app. Confere layout sem overflow, mais de seis
  linhas e três decisões disponíveis antes/depois de rolar; capturas mostram o
  marcador final. Dois testes instrumentados passaram no Medium_Tablet isolado
  (1280×800); dez PNGs na entrega compartilhada. O teste só aceita hardware de
  emulador e concede notificações nele antes de abrir a Activity.
- H2–H4: Control Center explica que o tablet mostra resumo, que Permitir autoriza
  execução neste PC, que comando/caminho e nome do projeto aparecem no tablet,
  e que a chave deve ser retirada do histórico da área de transferência após
  colar, se estiver ligado. Corrigidos ainda contraste de "Não criada" e a linha
  vazia de backup. Nenhuma alteração no contrato ou no instalador do mantenedor.
- Validação repetida após as alterações: JVM 480 casos/4 ignorados/zero falhas,
  lint zero erros/13 avisos, ambos os APKs compilados; Control Center 345/345,
  incluindo prévia sem gravação e ações dentro da página em 900×650.
- As primeiras fixtures de captura eram defeituosas: idle do Android não
  garantia a composição nova; agora há sincronização/nodes Compose. O primeiro
  harness WPF instanciava `App`, agendando o startup real e consultando o host
  preexistente. A versão corrigida usa `Application` simples com somente o tema,
  confirmação desabilitada e logs: uma janela, settings fictício não gravado.
  O incidente e os limites da verificação de settings/registro estão no relatório
  compartilhado; as imagens finais são separadas das imagens anteriores.
