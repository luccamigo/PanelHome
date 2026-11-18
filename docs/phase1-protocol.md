# Fase 1 — contrato aditivo e plano

Base: versão anterior do projeto. PRD e design: documentos internos e
`DESIGN-fase1.md`. A e B são branches independentes. APK API28 congelado não é saída desta branch.

## B1: PAN-15 e PAN-14, só leitura

- `hello.features`: `computer_stats` e `processes`. Host anuncia em `hello_ack`.
- Sem negociação, nenhum sampler novo roda nem mensagem nova é enviada.
- Telemetria existente continua a cada segundo, sem mudar seus campos.
- Com `computer_stats`, host envia a cada segundo:

```json
{"type":"network_rates","sampledAtUnixMs":1800000000000,"downloadBytesPerSecond":1024.0,"uploadBytesPerSecond":512.0,"adapters":[{"id":"opaque-id","name":"Ethernet","downloadBytesPerSecond":1024.0,"uploadBytesPerSecond":512.0}]}
```

Somente interfaces Up, sem loopback/tunnel. Primeira amostra, contador reiniciado ou erro = null,
nunca zero. Total nulo se não houver adaptador ou qualquer ativo estiver sem medição.
Não são enviados IP, MAC nem caminhos. `sampledAtUnixMs` usa relógio do host; os deltas usam relógio monotônico.

- Com `processes`, a cada 2 s:

```json
{"type":"processes","sampledAtUnixMs":1800000000000,"sort":"cpu","grouped":false,"terminateEnabled":false,"items":[{"name":"editor","pid":123,"startTimeUtcTicks":638000000000000000,"cpuPercent":1.5,"memoryBytes":1024,"owner":"current_user","protected":false,"count":1}]}
```

`pid`, `startTimeUtcTicks`, `cpuPercent`, `memoryBytes` são anuláveis. Dono = `current_user`, `other_user`,
`system` ou `unknown`. Sem leitura: métricas null e protegido. CPU normalizada por núcleos lógicos,
primeira amostra null. PID + início identificam o processo. Não há caminhos, comandos, usuário/SID nem ícones nesta etapa.
Agrupados têm PID/início null, count somado e protected true. Top 15 pela ordem escolhida, nulls ao fim.

- Escolha de ordenação/agrupamento (a próxima amostra reflete em até 2 s):

```json
{"type":"command","command":"processes.options","sort":"memory","grouped":true}
```

Somente `cpu`/`memory` e booleano obrigatório; recurso não negociado = `unknown_command`,
campos inválidos = `invalid_message`. As preferências ficam no app. Ordem durante toque é responsabilidade do modelo Android.

## Plano de entrega

1. B1: samplers de rede/processos injetáveis, seleção, negociação e fixtures; testes de deltas,
   reset, indisponibilidade, PID reutilizado, agrupamento e cliente antigo. UI Android por outra frente.
2. B2: PAN-9 usa HTTP exclusivamente 127.0.0.1 e chave; PAN-6 usa pipe próprio sem segredo (decisão da coordenação).
   Instalação do hook pelo Control Center com prévia e backup; testes só em caminhos temporários.
3. B3: pedidos de encerramento com revalidação de identidade/dono/proteção; gate desligado.
   Nenhuma liberação antes da revisão de segurança.

Testes sequenciais. Sem LAN/ADB/tablet/firewall, sem settings real, sem tocar PID1192, sem merge/push.
Custos/fps e comportamento do Windows/tablet reais dependem da janela de validação posterior.

## B2: avisos locais e hook Claude

PAN-9: desligado por padrão; nem cria TcpListener antes de ligar e gerar chave. Recusa processo elevado.
Porta padrão 8777, configurável (exclui 8765–8767 e 40001–40002). Rotas `POST /v1/notices` e
`POST /v1/values`; só `Content-Type: application/json`, `Content-Length` obrigatório, sem chunked.
Host exato 127.0.0.1/localhost + porta; sem Origin, Sec-Fetch-* ou cabeçalhos CORS/PNA.

Limites finais da revisão de segurança: corpo e headers 8192 B; 10/s com rajada20, avisos2/s com rajada5;
10 falhas de autenticação bloqueiam tudo por60s; 4 conexões, leitura inteira em5s.
Campos em code points após NFC/limpeza: título80, texto280 (até4 quebras), nome40, valor140,
unidade16, chave40, página40. Excesso é recusado. Tempo de tela3–30s; valoresTTL1–86400s;
12 valores, fila20 avisos/300s, só memória. Níveis info/success/warning/error; texto puro.

Chave: `panelos_` + base64url de32B aleatórios, somente Bearer. Resposta única de geração pelo pipe
de administração já existente; depois só sufixo4. Host persiste SHA256 em DPAPI CurrentUser com
ACL exclusiva; comparação FixedTimeEquals. Desligar removehash; religar pede chave nova.
Logs só de metadados; auth/taxa agregados10s. A retenção é a já existente no PanelHost.

Feature `local_notices`; snapshot1s:

```json
{"type":"local_notices","enabled":true,"sampledAtUnixMs":1800000000000,"notices":[{"id":"id","title":"Build","text":"Terminou","level":"success","key":null,"page":"computer","durationSeconds":5,"receivedAtUnixMs":1800000000000,"expiresAtUnixMs":1800000300000}],"values":[{"name":"Build","value":"OK","unit":null,"level":"success","updatedAtUnixMs":1800000000000,"expiresAtUnixMs":null}]}
```

Tablet deduplica ids; sem atualizar quando desconectado. Páginas canônicas `computer`, `processes`,
`notices` e ids exatos do perfil StreamDeck publicado; id inexistente é removido, nunca troca sozinho.

PAN-6 **não compartilha HTTP nem chave**: pipe `PanelOS.Claude.v1.<hash-do-SID>`, ambas as pontas
CurrentUserOnly, host confere SID por impersonação. 8KB/JSON estrito, leitura5s, 4 clientes e10/s/rajada20.
`PanelOS.ClaudeHook.exe` usa PermissionRequest no stdin, resume em360 codepoints (180+180 se cortado),
envia só projeto final/ferramenta/resumo. Sem host ou tablet: sai0 sem stdout em menos2s; timeout60s,
configurável30/60/90/120. Sem decisão, o fluxo normal do Claude permanece. AskUserQuestion/ExitPlanMode
continuam no terminal, pois exigem resposta estruturada. [Referência oficial](https://code.claude.com/docs/en/hooks#permissionrequest).

Feature `claude_permissions`:

```json
{"type":"claude_permissions","sampledAtUnixMs":1800000000000,"requests":[{"id":"32hex","project":"PanelOS","tool":"Bash","head":"inicio","tail":"fim","omitted":40,"expiresAtUnixMs":1800000060000,"contentHidden":false}]}
```

`contentHidden` (aditivo; ausente = `false`) vale `true` para Edit/Write/MultiEdit: o resumo mostra só o caminho, o
conteúdo da edição não é visível. Decisão D7 (mantenedor, reconfirmada em 16/11): Permitir continua ativo com `omitted > 0`
ou `contentHidden`; o tablet deve dizer "texto cortado" com `omitted` caracteres, ou "conteúdo da edição não visível".
O host registra o allow sobre resumo incompleto (`incompleteSummary=True`, nível Warning, sem conteúdo).

Fila: um por vez, prazo desde chegada. App envia `claude.shown` com requestId ao exibir;
depois `claude.respond`, mesmo requestId e decision allow/deny/defer. Host exige700ms desde shown,
primeiro da fila e geração da conexão atual; decisão vale uma vez. Defer, expiração, EOF do hook ou
desconexão não permitem nem negam. Sem evento confiável de resposta no terminal: PermissionRequest
não traz tool_use_id; portanto não afirmar "respondido no terminal". Logs: projeto, ferramenta, resultado;
nunca comando/arquivo. Conteúdo não persiste.

Instalador: prévia exata do settings e do caminho do backup; comparação contra arquivo anterior antes
de gravar; JSON inválido/duplicado não escreve. Remoção restaura bytes originais se inalterado;
se mudou/backup sumiu, remove só o comando exato PanelOS. Backups ficam guardados. Testes usam TEMP,
nunca `~/.claude/settings.json` real. Precisa reiniciar/recarregar o Claude conforme sua versão; não foi instalado nele.

## B3: encerramento atrás de revisão

`ProcessProtectionPolicy.Reviewed=false`: interruptor indisponível/desligado; admin `processes.configure`
recusa ligar com review_required. Nenhum pedido abre processo enquanto desligado.
Com futura revisão e ativação explícita: command processes.terminate, requestId32, pid e startTimeUtcTicks.
Somente item não agrupado oferecido nesta conexão; host reabre handle e confere início/PID, SID/sessão,
integridade, PPL, IsProcessCritical, imagem fora do Windows, pai do host e lista central versionada.
Falha de leitura recusa. TerminateProcess só nesse handle, nunca árvore.
Resposta `processes.terminate_result`: disabled, invalid_request, gone, protected, denied ou terminated. Um PID e horário de início que a última lista mostrou como protegido respondem `protected` (E1); só o que nunca foi listado, ou mudou de horário de início, responde `gone`.
Toda tentativa passa pelo log de hora/nome/PID/resultado, sem caminho/linha de comando.
Os testes usam alvos em memória; a API nativa de encerramento não foi executada nesta rodada.
