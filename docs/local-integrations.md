# Avisos locais e hook Claude (rascunho para revisão)

Recursos aditivos, só cabo nesta fase. O Wi-Fi continua na bancada sintética.
Uso diário depende da revisão de segurança e das medições no tablet. Não instalar o APK da fase1
na janela que usa o APK API28 congelado.

## Avisos

No Control Center, Integrações → Avisos, ligar e gerar chave. A chave aparece inteira uma vez;
o histórico da área de transferência do Windows pode guardá-la. Depois só aparece o sufixo.
Sem chave nada aceita pedidos; desligar fecha a porta e apaga o hash. Religando, gerar outra chave.
Não usar PanelHost elevado: o estado será `requires_unelevated`. Porta ocupada não derruba o host.

**Padrão: desligado.** Sem ligar no Control Center, a porta 8777 não escuta e o host registra `enabled=False`; isso é o esperado, não uma falha.
Endereço padrão: `127.0.0.1:8777`. Não há listener LAN, Tailscale, http.sys ou regra de firewall.
Outras contas do Windows alcançam loopback; a chave as barra. Guardar a chave em arquivo local
com ACL só do usuário, fora do repositório e da linha de comando. Os exemplos abaixo não contêm chave real.

Exemplo curl (o arquivo privado de cabeçalhos contém `Authorization: Bearer <chave>`; protegê-lo por ACL):

```powershell
curl.exe --request POST http://127.0.0.1:8777/v1/notices --header '@C:\caminho-privado\panelos-headers.txt' --header 'Content-Type: application/json' --data-binary '@C:\caminho\aviso.json'
```

`aviso.json`, exemplo fictício:

```json
{"title":"Build terminou","text":"Todos os testes passaram.","level":"success","key":"build","page":"computer"}
```

Exemplo PowerShell/script local do Zelador (executar somente no PC; não é o Zelador do servidor):

```powershell
$panelosKey = (Get-Content -LiteralPath 'C:\caminho-privado\panelos-key.txt' -Raw).Trim()
$panelosBody = @{ name = 'Containers'; value = '12 ativos'; level = 'success'; ttlSeconds = 300 } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:8777/v1/values' -ContentType 'application/json' -Headers @{ Authorization = 'Bearer ' + $panelosKey } -Body ([Text.Encoding]::UTF8.GetBytes($panelosBody))
Remove-Variable panelosKey
```

Script real e credenciais do Zelador ficam no repositório dele. PanelHost nunca recebe credenciais do servidor.
O API responde com status e motivo (401 chave, 413 corpo, 429 taxa, 400 campos, 409 cartões).
Corpo ≤8KB; campos e demais limites em `phase1-protocol.md`. Avisos/valores são texto puro, sem HTML/link/ação.

## Claude Code

O instalador mostra o conteúdo exato antes de gravar e o caminho do backup. Não muda hooks de outros programas.
Remover restaura o backup se o arquivo estiver igual; caso contrário, tira só a entrada PanelOS. Mantém backups.
Não há senha/chave: hook fala por pipe CurrentUserOnly, e o host confere SID. Não passa pela porta HTTP.
No diretório do Control Center devem existir PanelOS.ClaudeHook.exe/.dll/.deps.json/.runtimeconfig.json;
o build copia esses arquivos. O hook é autocontido (net8.0 puro, sem referência a `PanelOS.Windows.dll` nem às DLLs do WinRT):
esses quatro arquivos bastam na pasta estável. `ClaudeHookDeploymentTests` instala o hook real num diretório isolado,
confere que o `deps.json` só nomeia arquivos presentes e executa o exe instalado; uma dependência esquecida falha ali. Não executar o hook manualmente com comandos reais para "testar" permissões.

**Pasta estável (P2):** o `settings.json` nunca aponta para o `bin` do build. A instalação copia os 4 arquivos para
`%LOCALAPPDATA%\PanelOS\hook\`, e o comando gravado aponta para `PanelOS.ClaudeHook.exe` dali. A prévia lista cada arquivo com o
SHA-256; a instalação lê os arquivos uma vez, recusa se o hash não bater com a prévia e confere a cópia gravada. Rebuild ou troca de
branch não mudam o que roda: o estado mostra "desatualizado" (`Outdated`) e só reinstalar atualiza. Cópia alterada depois aparece como
`HookFilesChanged`. Remover apaga só as cópias que ainda têm o hash instalado e a pasta, se ficar vazia.

Se o host/tablet estiver fora, o hook sai sem decisão em ≤2s; caso contrário, aguarda30/60/90/120s (padrão60).
Falha/expiração/Deixar no PC preservam a pergunta no terminal. Várias sessões entram numa fila com prazos independentes.
O PC corta o resumo em180+180 pontos de código; Permitir continua disponível com aviso, decisão D7.
AskUserQuestion e ExitPlanMode ficam no terminal: suas respostas exigem dados estruturados.
Sem evento confiável de resposta no terminal, só se afirma que o pedido expirou/cancelou.
Se o pedido sair da lista após o tablet enviar uma resposta, a mensagem diz "Resposta enviada".
Isso não confirma aplicação: expiração, EOF ou resposta no terminal também podem encerrar o pedido no PC.
Referência de contrato: [hooks oficiais](https://code.claude.com/docs/en/hooks#permissionrequest).

## Processos

A lista é só leitura até a revisão. O interruptor de encerramento vem desligado e indisponível nesta entrega.
A lista conservadora está em `ProcessProtectionPolicy.cs`; não retirar nomes sem revisão. O app OpenBot e os helpers dele (nome `OpenBot*`, `cua-driver` e qualquer processo nas pastas `OpenBot` de Programs, AppData e Program Files, como os runtimes dos agentes) também são protegidos. Mesmo após liberação,
o PC revalida tudo no mesmo handle e encerra um único processo, nunca árvore. O tablet não recebe caminho/linha de comando.
Não houve encerramento real nem instalação no Claude nesta rodada; testes usam alvos em memória e arquivos TEMP.
