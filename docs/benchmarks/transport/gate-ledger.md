# Registro dos gates do carrier nativo

Estado corrente de cada gate da promoção do USB nativo. Este arquivo é vivo:
cada sessão de bancada atualiza a linha do gate que tocou e diz com que
evidência. O relatório de 2026-08-19 continua sendo o registro daquela sessão e
não é editado retroativamente.

Regra: um gate só muda para PASS com evidência citável — linha de log, artefato
de benchmark ou captura. "Funcionou quando eu olhei" não fecha gate.

## Estado

| Gate | Estado | Desde | Evidência |
| --- | --- | --- | --- |
| A — PLMX v2 | **PASS** | 2026-08-26 | `[PLMX] session established role=accessory session=71B30C… version=2`; resync limitado descartou 178 bytes obsoletos e recuperou (2026-08-19) |
| B — Desktop | **PASS** | 2026-08-26 | display virtual em 1280×800, H.264 iniciado, imagem do Windows visível no tablet |
| C — negociação 60 FPS | **PASS** | 2026-08-26 | `max=1280x800 maxFps=60 codecMask=0x3` → `ACK … at 60 FPS, codec=2` → `effectiveSessionFps=60`, sem clamp |
| C — 60 FPS sustentado | **PASS** | 2026-08-26 | janelas com `scheduledFrames=300 publishedFrames=300 missedDeadlines=0 sequenceGap=0` e `driverCapture=hostReceive=send=60,0`; `seqGap` 0–3 em 13 janelas, `frameInterval` p50 16,66 ms |
| D — estabilidade de vídeo | **PARCIAL** | 2026-08-26 | 16.441 de 16.447 access units renderizados (99,96%), `codecErrors=0`, zero linhas W/E, sem reinício de decoder, ~5,5 min contínuos; falta o soak longo |
| E — controle sob carga | **PASS** | 2026-08-19 | 0,244–0,430 ms de latência média de controle com o canal de vídeo saturado |
| F — toque | **PASS (quase)** | 2026-08-26 | DOWN/MOVE/UP encaminhados, `rejection=none`, `contentRect=(41,6..1237,4 × 747,0)`, sem erros de toque; varredura de cantos e bordas ainda devida |
| G — Stream Deck | **NÃO EXECUTADO** | — | layout publicado (`revision=51ba1c107f70ea5c pages=1`), nenhuma ação invocada |
| H — cable pull, lado do disconnect | **PASS** | 2026-08-19 | exceção concreta, teardown limpo, sem crash, 3,38% de CPU |
| H — cable pull, lado do reconnect | **NÃO EXECUTÁVEL** | 2026-08-26 | limitação do tablet de teste; ver [`../../transport-device-limitations.md`](../../transport-device-limitations.md) |
| I — campanha de reconexão | **NÃO EXECUTÁVEL** | 2026-08-26 | mesma limitação; decisão registrada, não bloqueia a promoção |
| J — soak | **NÃO EXECUTADO** | — | depende de uma sessão longa, não do Gate I |

## Sessão de 2026-08-26

Host elevado já em execução (PID do processo em uso, iniciado 18:28) em `--transport usb`;
nenhum listener TCP em 8765/8766 confirmou o carrier. PanelHome reiniciado e
reconectado pelo carrier nativo, com adb vivo no MI_01 para instrumentação.

O que a sessão acrescentou:

- **Seleção de carrier com acessório já anexado funciona.** Ao subir com o
  acessório anexado e permitido, o app escolheu o carrier nativo sem receber
  `USB_ACCESSORY_ATTACHED` — que é exatamente o caso que
  `TransportSelection.shouldUseNativeUsb` existe para cobrir.
- **O caminho de recuperação dispara como projetado.** Em uma tentativa cujo
  handshake não fechou, o app registrou
  `Native USB down for 8000ms … restarting the process, attempt 1/5` e
  reiniciou. O contador limitou a tentativa, sem laço.
- **O encoder tem folga contra o orçamento de 60 FPS.** Access units chegaram
  com `encode=4,244ms` a `encode=6,969ms`, contra um orçamento de 16,7 ms por
  quadro. Isso é a informação que faltava para ler o Gate C: se 60 FPS
  sustentado falhar, o resultado é de captura/pacing, não do encoder.
- **Nenhuma falha de decoder.** `codecErrors=0` do início ao fim,
  `framesRendered` acompanhando `accessUnitsQueued` com a diferença esperada de
  latest-frame-wins (36 renderizados para 42 enfileirados).

O que a sessão **não** fechou, e por quê:

- **Gate C sustentado.** O conteúdo do display virtual era uma janela quase
  estática, então `networkFps` ficou entre 0,6 e 2,7 — o mesmo regime de 8–28
  FPS já registrado em 2026-08-19. Isso não é falha: sem conteúdo novo, não há
  quadro novo. Fechar o gate exige conteúdo dinâmico **no display virtual**,
  seja movendo uma janela de vídeo para ele, seja subindo o host com
  `--video-test-pattern-dynamic`.
- **Gate G.** Fechar exige invocar um botão do Stream Deck com a sessão de vídeo
  ativa e confirmar que a ação executou no Windows. Não foi exercitado.

## Gate C sustentado — primeira sessão, conteúdo intermitente, 2026-08-26

Sessão do operador, carrier nativo, `encoder=software`, 1280×800, alvo 60 FPS,
oito janelas de ~5 s, sem conteúdo contínuo no display virtual. Os três
estágios do artefato (`schemaVersion` 3) separam onde o quadro se perde — e,
como se vê abaixo, só um deles é perda de verdade:

| Janela | driverCapture | hostReceive | send | sequenceGap | missedDeadlines |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 8,8 | 6,6 | 6,4 | 11 | 257 |
| 2 | 49,1 | 36,9 | 36,9 | 61 | 55 |
| 3 | 55,2 | 41,8 | 42,0 | 67 | 24 |
| 4 | 54,4 | 43,0 | 42,8 | 57 | 29 |
| 5 | 56,2 | 43,1 | 43,1 | 66 | 19 |
| 6 | 56,2 | 41,6 | 41,8 | 73 | 19 |
| 7 | 57,7 | 42,7 | 42,5 | 75 | 12 |
| 8 | 44,1 | 32,7 | 32,7 | 57 | 80 |

Parecia haver **duas perdas**. Há uma. Manter os estágios separados é o que
permitiu descobrir isso:

**Perda 1 — driver, 60 → 48-57: não é perda.** Esta tabela foi medida com o
desktop em conteúdo intermitente, e `missedDeadlines` **não** mede atraso do
scheduler. Em `SwapChainProcessor.cpp`, o contador é somado quando o deadline
chega sem quadro novo staged:

```cpp
if (!m_StagingTexture || !m_HasPendingStaging)
{
    InterlockedAdd64(&m_MissedDeadlineCount, Decision.ElapsedDeadlines);
    return;
}
```

Ou seja: mede ausência de conteúdo, não atraso. A janela 1 (`publishedFrames=44`,
`missedDeadlines=257`) é uma tela praticamente parada, não um driver falhando.
`deadlineLateness` só é registrado em publicação bem-sucedida
(`RecordDeadlineLateness` depois de `TryPublishPendingStaging`), então o p50 de
10 ms aqui vem de uma amostra minúscula e não é comparável com o da sessão
seguinte.

Conclusão corrigida: **não há trabalho de pacing pendente no `CaptureScheduler`.**
O estágio do driver só pode ser avaliado com conteúdo contínuo, e é o que a
sessão abaixo faz.

**Perda real — host, 48-57 → 33-43.** Esta existe e tem causa raiz identificada.
`sequenceGap` bate exatamente com `publishedFrames − recebidos` em todas as
janelas (por exemplo, janela 7: 289 publicados, 42,7 × 5,0 = 214 recebidos,
gap 75). O host **não** está atrasado — `driverPublishedSequence` e
`hostReadSequence` são idênticos em todas as janelas. Ele simplesmente não
amostra rápido o bastante.

### Causa raiz: o poll roda a 64 Hz, não aos 121 Hz de projeto

`VideoFramePollingCadence` pede `60 × 2 + 1 = 121` polls/s justamente para que
jitter de escalonamento não vire quadro perdido — o comentário no arquivo diz
isso. O log mostra `hostPollFps=64,4` em todas as janelas, estável.

Medido em 2026-08-26 com um `PeriodicTimer` de 8,2645 ms, o mesmo que
`ProduceRawFramesAsync` usa:

```
como esta agora          resolucao= 1,0000ms  pollFps medido=  63,9
com timeBeginPeriod(1)   resolucao= 1,0000ms  pollFps medido= 125,0
```

A resolução de timer no Windows é **por processo** desde a versão 2004. Outro
processo elevar a resolução global para 1 ms não ajuda: o PanelHost precisa
pedir por si. Sem esse pedido, o `PeriodicTimer` fica na granularidade padrão de
~15,6 ms e tica a 64 Hz — que é exatamente o `hostPollFps` observado.

Amostrar a 64 Hz um publicador de ~55 Hz produz batimento: alguns intervalos
pegam duas publicações e o host, sendo latest-only, descarta uma; outros não
pegam nenhuma. `noNewFramePolls` de 107 a 159 por janela é a outra metade do
mesmo efeito.

O teste que existe
(`H264PipelineTests`) verifica apenas a aritmética `PollsPerSecond(60) == 121`.
Nada compara a cadência de projeto com a cadência real, e é por isso que isto
passou no CI desde sempre.

### Segunda sessão — a primeira medição válida do estágio do driver

**Única diferença em relação à sessão anterior: um vídeo do YouTube rodando no
display virtual, sem nenhuma mudança de código.** É essa a variável, e ela é a
razão pela qual a tabela acima não media o que parecia medir.

Sessão limpa (`sessions=1`, `reconnects=0`, `errors=0`, `handshakeFailures=0`,
contra 17/3/3/13 da primeira), cinco janelas:

| driverCapture | hostReceive | send | sequenceGap | missedDeadlines | hostPollFps |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 59,8 | — | — | 25 | 2 | — |
| 58,2 | 53,0 | 53,0 | 26 | 9 | 64,6 |
| 56,5 | 48,3 | 48,3 | 41 | 18 | 64,5 |
| 58,1 | 50,9 | 50,9 | 36 | 10 | 64,5 |
| 58,8 | 53,2 | 53,2 | 28 | 6 | 64,4 |

**O driver estava sempre certo; faltava conteúdo.** Com quadro novo em todo
deadline, `missedDeadlines` cai para 2–18 em 301 e o driver publica 56,5–59,8
FPS. Não houve melhora de pacing porque não havia pacing a melhorar — a leitura
anterior atribuía ao scheduler um contador que mede outra coisa. O estágio de
captura está no alvo e sai da lista de pendências.

**A perda do host é real e é a única.** `hostPollFps` segue em 64,4–64,6 em
todas as janelas: a cadência de poll não mudou, e agora dá para ver que ela é o
teto. `sequenceGap` continua batendo com `publicados − polls produtivos` em
cada janela (janela 5: 294 publicados, 323 polls, 56 vazios, 267 produtivos,
gap 28). Com o driver alimentado, o batimento come 25–41 quadros por janela.

Efeito líquido: `send` em 48,3–53,2 FPS contra 56,5–59,8 publicados. O déficit
é inteiramente a cadência de poll.

Detalhe que reforça o diagnóstico: o vídeo do YouTube manteve a resolução
global de timer em 1 ms durante toda a sessão, e ainda assim `hostPollFps`
ficou em 64. É a confirmação prática de que a resolução é por processo — o
PanelHost precisa pedir por si, e não pede.

Sinais de saúde na mesma sessão: `controlLatencyMs` caiu de 1,188 para 0,579
com o vídeo carregando o canal, `videoDrops=0`, `staleFrames=0`,
`sequenceGaps=0` no transporte, e o resync limitado descartou os mesmos 178
bytes do relatório de 2026-08-19 — reprodutível.

### Correção aplicada — a cadência de poll subiu, o Gate C ainda não fechou

`TimerResolutionScope` (commit anterior) pede 1 ms de resolução pelo tempo da
sessão de vídeo e devolve no teardown. Verificado em sessão real, host não
elevado, `--fake-desktop --video-test-pattern-dynamic --video-target-fps 60`:

```
[VideoPacing] Timer resolution raised to 1ms for this session; poll target 121 Hz.
[VideoPacing] targetFps=60 actualHostReceiveFps=124,4 hostPollFps=124,4 noNewFramePolls=0
[VideoPacing] targetFps=60 actualHostReceiveFps=124,1 hostPollFps=124,1 noNewFramePolls=0
[VideoPacing] targetFps=60 actualHostReceiveFps=124,5 hostPollFps=124,5 noNewFramePolls=0
```

`hostPollFps` foi de 64,4 para 124,1–124,5, contra um alvo de projeto de 121, e
`noNewFramePolls` caiu a zero. A metade do diagnóstico que dizia respeito à
cadência está confirmada.

**O que esta verificação não prova.** A fonte é o padrão de teste, que
sintetiza um quadro a cada poll — por isso `driverCapture` aparece igual a
`hostPollFps`. Esses 124 FPS não são taxa de captura real e não fecham o
Gate C. O que resta medir é a sessão com driver, host elevado e conteúdo
contínuo: é lá que `sequenceGap` precisa cair de 25–41 para perto de zero e
`send` passar a acompanhar `driverCapture` em 57–59.

### Sessão com conteúdo 60 fps — o host tem um teto fixo que a cadência não explica

Host elevado, driver real, vídeo 60 fps em tela cheia no display virtual.
**A correção não estava ativa neste run** (`hostPollFps=64,4–64,8`; o binário
Release em uso era de antes dessa correção), então isto mede o
comportamento antigo — mas com conteúdo que finalmente alimenta 60 FPS:

| driverCapture | hostReceive | send | sequenceGap | missedDeadlines |
| ---: | ---: | ---: | ---: | ---: |
| 46,7 | 33,3 | 33,3 | 67 | 67 |
| 50,0 | 34,6 | 34,6 | 77 | 50 |
| 51,9 | 35,1 | 35,3 | 84 | 45 |
| 52,7 | 34,5 | 34,7 | 91 | 36 |
| 57,1 | 34,7 | 34,7 | 112 | 14 |
| 58,2 | 35,9 | 35,7 | 112 | 41 |
| 59,8 | 34,2 | 34,0 | 128 | 2 |

**O estágio do driver atinge o alvo.** Com conteúdo 60 fps a captura chega a
59,8 FPS com `missedDeadlines=2` em 300 slots. Não há mais dúvida sobre esse
estágio.

**O estágio do host tem um teto fixo, não proporcional.** `hostReceive` fica
travado em 34,5 ± 1,3 enquanto `driverCapture` varia de 46,7 a 59,8 — quanto
mais o driver publica, mais o host descarta, e `sequenceGap` sobe de 67 para
128 exatamente para compensar.

Isto **não é explicado pela cadência de poll sozinha**, e a hipótese anterior
precisa ser corrigida: com o poll a 64,4 Hz (período 15,5 ms) e a publicação a
59,8 Hz (período 16,7 ms), o período de amostragem é *menor* que o de
publicação, e um amostrador uniforme pegaria quase todos os quadros. Ele pega
metade: 323 polls por janela, 151 sem quadro novo.

`frameInterval` p50 fica em 32,8–33,3 ms em todas as janelas — exatamente dois
períodos de publicação. O host toma sistematicamente um quadro sim, um não, o
que é padrão de gating, não de batimento.

Conclusão honesta: a cadência de 64 Hz contra 121 de projeto é um defeito real
e medido, e a correção continua justificada — mas ela pode não ser suficiente,
e este run não a testou. Existe um segundo fator no caminho de leitura do
driver, ainda não identificado.

### Correção validada em bancada — o estágio do host fecha

Host elevado, driver real, correção ativa. Três janelas:

| driverCapture | hostReceive | send | sequenceGap | drop | hostPollFps | noNewFramePolls |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 31,8 | 31,8 | 31,8 | **0** | 0 | 125,0 | 467 |
| 30,0 | 30,0 | 30,0 | **0** | 0 | 124,3 | 472 |
| 30,0 | 30,0 | 30,0 | **0** | 0 | 124,3 | 472 |

`hostReceive` é **idêntico** a `driverCapture` e `sequenceGap` é **zero**, não
"próximo de zero". O host passa a entregar 100% do que o driver publica. A
previsão registrada acima se confirma, e o estágio do host sai da lista de
pendências do Gate C.

`noNewFramePolls` de 467 em ~625 polls é o comportamento desejado, não
desperdício: amostrar bem acima da taxa de publicação é exatamente o que
garante que nenhum quadro seja pulado. `driverRead` caiu de 2,75 ms para
0,93 ms de média (p50 de 3,13 para 0,32 ms), porque a maioria dos polls agora
retorna "nada novo" de imediato.

**Retratação da entrada anterior.** O "segundo fator no caminho de leitura do
driver" não existe. O teto fixo de ~34,5 FPS era a cadência de poll, e a
dúvida levantada na entrada anterior estava errada.

Fica registrado um resíduo não explicado, porque ele não deve ser varrido para
baixo do tapete: um amostrador *uniforme* a 64 Hz (período 15,5 ms) contra uma
publicação de 16,7 ms deveria pegar quase todos os quadros, e não metade. O
padrão observado — 50% de polls vazios e captura de um quadro sim, um não —
implica que os polls a 64 Hz não eram uniformes. A causa e a correção estão
estabelecidas por medição; a micromecânica dessa não-uniformidade não está.

### O que ainda falta para o Gate C

Este run rodou com conteúdo de 30 fps: `publishedFrames=150` em 300 slots,
`swapAcquire=30,0fps`, `throttleSkipped=0,4fps` — o compositor só apresentou 30
quadros por segundo, então `driverCapture=30,0` é o teto do conteúdo, não do
pipeline. `missedDeadlines=150` continua significando ausência de conteúdo.

Falta uma única medição: conteúdo 60 fps **com a correção ativa**. Com o
estágio do driver já demonstrado em 59,8 FPS e o estágio do host agora sem
perda, o resultado esperado é `driverCapture ≈ send ≈ 58-60` com
`sequenceGap=0`.

### Conteúdo 60 fps com a correção ativa — medição no fim da cadeia

Mesma sessão, vídeo 60 fps no display virtual. Medido no **tablet**, que é o
consumidor final: se ele renderiza N FPS, então `send` e `driverCapture` foram
pelo menos N.

```
receiveFps=54,0  renderFps=54,0  decode p50=17,79ms  receiveToRender=0,08ms
receiveFps=59,2  renderFps=59,2  decode p50=17,88ms  receiveToRender=0,11ms
receiveFps=54,7  renderFps=54,7  decode p50=17,41ms  receiveToRender=0,07ms
receiveFps=53,0  renderFps=53,0  decode p50=17,49ms  receiveToRender=0,20ms
```

`renderFps` é **igual** a `receiveFps` em todas as janelas: o tablet renderiza
tudo o que recebe. `receiveToRender` fica abaixo de 0,2 ms, sem enfileiramento.
Acumulado da sessão: `accessUnitsQueued=16447`, `framesRendered=16441`
(99,96%), `codecErrors=0`, zero linhas W/E.

O `decode` p50 caiu de 32 ms para 17,5 ms quando o conteúdo passou de 30 para
60 fps, o que confirma que essa métrica acompanha o intervalo entre quadros e
não custo real de decodificação — não é gargalo e não deve ser lido como tal.

**Comparação com o mesmo tablet antes da correção:** 29,6–30,3 FPS recebidos
com conteúdo 30 fps; 53,0–59,2 agora. O pico de 59,2 mostra que a cadeia
inteira — captura, encode, PLMX sobre USB, decode e render — sustenta
essencialmente 60.

**O que ainda impede marcar PASS.** Falta correlacionar estas janelas com
`driverCapture`, `send` e `sequenceGap` do host. A variação de 53,0 a 59,2
precisa ser atribuída: se `driverCapture` variar junto, é conteúdo e o pipeline
está limpo; se `driverCapture` ficar em 60 enquanto `send` cai para 54, sobrou
perda. A regra deste arquivo é evidência citável, e essa ainda não existe.

### Gate C sustentado — PASS, 2026-08-26

Host elevado, driver real, correção ativa, vídeo 60 fps no display virtual.
Treze janelas consecutivas de ~5 s:

| driverCapture | hostReceive | send | seqGap | drop | hostPollFps | missedDeadl. | frameInterval p50 | jitter |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 60,0 | 59,8 | 60,0 | 1 | 1 | 125,1 | 0 | 16,66 | 0,29 |
| 59,9 | 59,5 | 59,3 | 2 | 2 | 124,0 | 1 | 16,63 | 0,47 |
| 58,6 | 58,6 | 58,8 | 0 | 0 | 125,1 | 7 | 16,67 | 0,56 |
| 60,1 | 59,9 | 59,7 | 1 | 1 | 124,8 | 0 | 16,66 | 0,21 |
| 60,0 | 59,8 | 59,8 | 1 | 1 | 125,0 | 0 | 16,66 | 0,24 |
| 59,8 | 59,8 | 59,8 | 0 | 0 | 125,0 | 1 | 16,67 | 0,27 |
| 60,1 | 60,1 | 60,1 | 0 | 0 | 125,1 | 0 | 16,66 | 0,15 |
| 60,0 | 60,0 | 60,0 | 0 | 0 | 124,9 | 0 | 16,66 | 0,15 |
| 57,6 | 57,0 | 57,2 | 3 | 3 | 124,9 | 12 | 16,68 | 1,03 |
| 60,1 | 60,1 | 59,9 | 0 | 0 | 124,9 | 0 | 16,66 | 0,18 |
| 59,4 | 59,4 | 59,6 | 0 | 0 | 125,0 | 3 | 16,67 | 0,39 |
| 54,5 | 54,5 | 54,3 | 0 | 0 | 124,9 | 28 | 16,71 | 1,90 |
| 59,0 | 59,0 | 59,0 | 0 | 0 | 125,0 | 5 | 16,67 | 0,42 |

A evidência mais direta são as janelas em que
`scheduledFrames=300, publishedFrames=300, missedDeadlines=0, sequenceGap=0` com
`driverCapture=hostReceive=send=60,0`: **todo quadro agendado foi capturado,
lido, codificado e enviado.** Nenhum perdido em nenhum estágio.

`sequenceGap` fica entre 0 e 3, com seis janelas em exatamente 0.
`frameInterval` p50 é 16,66–16,71 ms — 60,0 Hz — e o `jitter` médio caiu de
12–16 ms para 0,15–1,90 ms.

**A variação abaixo de 60 é conteúdo, e isso agora é demonstrável em vez de
suposto.** As três janelas com `driverCapture` menor (58,6 / 57,6 / 54,5) são
exatamente as com `missedDeadlines` maior (7 / 12 / 28), e nessas mesmas
janelas `sequenceGap` continua 0 ou 3. O pipeline entrega o que o compositor
apresenta; quando o compositor apresenta 60, ele entrega 60.

Latência ponta a ponta do host: `captureToSend` 24,3–27,1 ms. Saúde da sessão:
`sessions=1`, `reconnects=0`, `errors=0`, `handshakeFailures=0`,
`sequenceGaps=0`, `videoDrops=0`, `staleFrames=0`, ao longo de 484 MB de vídeo
transferidos. `controlLatencyMs` caiu para 0,359 sob carga de vídeo, contra
0,55–1,19 antes.

Comparação do caminho completo, mesmo tablet e mesmo conteúdo de classe:

| | antes | depois |
| --- | ---: | ---: |
| hostPollFps | 64,4 | 125,0 |
| sequenceGap por janela | 25–128 | 0–3 |
| send | 33–43 | 57–60 |
| jitter médio | 12–16 ms | 0,2–1,9 ms |

### Previsão a verificar na próxima sessão de bancada

Se o diagnóstico estiver correto, elevar a resolução de timer do processo
durante a sessão de vídeo deve levar `hostPollFps` de ~64 para ~121 e colapsar
`sequenceGap` em direção a zero, com `send` passando a acompanhar
`driverCapture` em torno de 57–59 FPS. Se `sequenceGap` **não** cair depois
disso, o diagnóstico está errado e a perda está em outro lugar — o que também é
informação.

### Correções possíveis

1. **`timeBeginPeriod(1)` durante a sessão de vídeo.** Barato, reversível,
   confirmado acima como suficiente para atingir 125 Hz. Custa consumo de
   energia enquanto a sessão está aberta, e por isso deve ser liberado no fim
   dela, não no início do processo.
2. **Um IOCTL que espera pelo próximo quadro**, em vez de poll. Elimina o
   batimento por construção e reduz `noNewFramePolls` a zero, mas exige uma
   revisão do contrato do driver (v6) — hoje `GET_FRAME_INFO` só suprime
   retransmissão via `LastSequence`, não bloqueia.

A correção 1 destrava a medição do Gate C; a 2 é a resposta estrutural. Nenhuma
das duas foi aplicada — este registro documenta o diagnóstico, não uma mudança.

## O que falta, e o que cada coisa exige

| Pendência | Exige |
| --- | --- |
| Gate C sustentado | corrigir a cadência de poll (ver causa raiz acima) e remedir com conteúdo contínuo; é a única pendência do gate — o estágio do driver já está no alvo |
| Gate D | uma sessão longa com vídeo em movimento, observando reinício de decoder e tela preta |
| Gate G | invocar uma ação do Stream Deck sob vídeo e confirmar o efeito no Windows |
| Gate F, cantos e bordas | varredura de toque com o dedo; `adb shell input` não serve, porque contorna o digitalizador e é justamente o mapeamento de coordenadas que está sendo checado |
| Gate J | soak de uma hora depois que C, D e G fecharem |

O procedimento de cada fase está em
[`raw/final-validation/20260819-operator-runbook.md`](raw/final-validation/20260819-operator-runbook.md).

## Achados colaterais desta sessão

Dois defeitos apareceram enquanto a bancada era montada. Nenhum é gate, os dois
afetam operação.

**O pipe de management é inalcançável quando o PanelHost roda elevado.**
`PanelHostManagementServer.CreatePipe` usa `PipeOptions.CurrentUserOnly` e o
comentário afirma que "an elevated PanelHost and an unelevated Control Center
from the same Windows account may connect". Medido: um cliente não elevado do
mesmo usuário recebe `ERROR_ACCESS_DENIED` — com `CurrentUserOnly`, sem ele e
com o pipe aberto somente para leitura. `CurrentUserOnly` resolve o DACL, mas o
rótulo de integridade do objeto criado por um processo elevado bloqueia um
cliente de integridade média. Como o modo Desktop é documentado como exigindo
host elevado, o Dashboard e o Diagnóstico do Control Center ficam sem dados de
tablet, display e vídeo justamente na configuração normal.

**Um segundo PanelHost morre com stack trace em vez de mensagem.** Subir um
PanelHost enquanto outro detém o pipe termina em
`UnhandledException: UnauthorizedAccessException` vindo de
`PanelHostManagementServer.RunAsync`, propagada até o boundary do `Program.cs`.
O host não sobrevive à falha do servidor de management e não distingue "já
existe um agente" de uma falha real.
