# Listener de bancada — código para revisão, nunca executado em rede

Branch de tarefa. Listener de **sessão**, somente leitura e
sintético. O listener nunca foi executado na LAN, e nenhuma regra de firewall foi
criada. Os únicos sockets abertos até hoje foram os dos testes da suíte, em
`127.0.0.1`, com autorização expressa (ver "Testes de socket em loopback"). Os
comandos abaixo são propostas: exigem a revisão de segurança antes da primeira
execução e a aprovação do mantenedor para a janela, o firewall e a parada do host
em execução.

## O que existe

| Peça | Para quê |
|---|---|
| `PanelHost.exe --wifi-bench …` | Listener de sessão com janela de tempo obrigatória (nunca padrão) |
| `PanelHost.exe --wifi-bench-check --wifi-bind <ip>` | Só leitura: diz se a interface do IP é física, Up e **já** Private, com o mesmo critério do listener, sem alterar nada |
| `scripts/wifi-bench-preflight.ps1` | Pré-voo só de leitura do host: hash do exe, `--wifi-bench-check`, perfil, regras existentes e regras amplas |
| `PanelHost.exe --wifi-bench ... --wifi-pair-port <porta>` | Habilita, só durante a janela, a administração do pareamento pelo Control Center (pipe local) e a porta de pareamento efêmera; ver [docs/wifi-tablet.md](wifi-tablet.md) |
| `PanelHost.exe --wifi-bench-init` | Cria a identidade do PC (raiz, servidor, chaves CNG) uma única vez |
| `PanelHost.exe --wifi-bench-issue-peer …` | Emite e ativa um par de teste a partir de uma chave **pública**; grava a oferta pública |
| `PanelHost.exe --wifi-bench-revoke …` | Revoga um par de teste (o hash da chave nunca mais é aceito) |
| `PanelHost.exe --wifi-bench-recover …` | Prévia e, com confirmação, recuperação após saída suja do dono |
| `PanelHost.WifiBenchClient.exe` | Cliente de teste para o outro PC: `keygen`, `run`, `cleanup` |

Nada disso é produto Wi-Fi: não há descoberta, tela, câmera, vídeo real, Stream
Deck, Desktop nem pareamento por rede. O cabo continua sendo o único caminho do
produto.

## Condições C1–C9 da revisão de segurança (cumpridas no código)

- **C1:** admissão síncrona antes de qualquer task ou TLS. Quatro trabalhadores no
  total, inclusive sessões autenticadas; doze admissões por minuto móvel; rajada
  de quatro e reposição de uma a cada cinco segundos. Sem fila de espera, backlog
  TCP 4. TLS e PLMX têm prazo individual de cinco segundos (isso fecha o
  slowloris). O limitador deve ser compartilhado com qualquer porta de pareamento
  futura; não há segunda porta nesta bancada.
- **C2:** opt-in `--wifi-bench`, IPv4 RFC1918 literal em interface Up
  Ethernet/Wi-Fi. O Windows deve informar `Win32_NetworkAdapter.PhysicalAdapter=true`
  e `MSFT_NetConnectionProfile.NetworkCategory=1` (Private) pelo mesmo
  InterfaceIndex; sem inferência pelo nome e sem fallback em erro ou ambiguidade.
  Bind exclusivo no IP exato, sem curinga, IPv6, mDNS, UPnP ou NAT-PMP. Origem só
  da sub-rede diretamente ligada, recusando o próprio IP, a rede, o broadcast e
  IPv4 mapeado em IPv6. Mudança de endereço cancela a janela; perfil e interface
  são reconferidos a cada 1 s, mais o timeout WMI de 2 s por enumeração. Logo, uma
  mudança de perfil para Public pode levar cerca de 3 s para fechar a janela
  (limite conhecido, L5 da revisão de segurança; a regra de firewall também é só Private).
  O código de produção **não aceita loopback**: `WifiBenchOptions.Validate` e
  `WifiLanBinding` o recusam.
- **C3:** o código não mexe no firewall. A regra proposta está no roteiro.
- **C4:** caminho único `WifiTls.AuthenticateSessionAsync` (mTLS TLS 1.3, ALPN,
  sem retomada) e depois PLMX com `CarrierLimits.WifiV1` explícito. Não há porta de
  pareamento. Só entra par já emitido e ativado localmente pelos comandos acima.
- **C5:** dispatcher próprio, sem serviços Desktop, toque, mídia ou Stream Deck.
  Exige o bit local `PanelReadCapability=1`, valida o hello e anuncia zero
  funcionalidades. Telemetria explicitamente **sintética, com sensores nulos**.
  Só aceita ping/pong estritos; qualquer comando ou entrada desconhecida fecha a
  sessão antes de qualquer efeito. Vídeo recebe FIN e o Control continua. Limites
  de Control: 240 mensagens/s (rajada 480) e 2 MiB/s (rajada 2 MiB), além do carrier.
- **C6:** o modo Wi-Fi retorna antes de construir host, cabo ou broker. O mutex de
  instância permanece. Não há arbitragem dinâmica com o cabo: exige a flag
  `--wifi-cable-disconnected` e o cabo fisicamente desconectado pelo operador.
- **C7:** recusa processo elevado e Windows anterior ao 11. Heartbeat de 15 s sem
  hello/ping/pong válido cancela e fecha o stream físico (byte parcial e entrada
  inválida não renovam o prazo). O cancelamento normal fecha o listener e os
  streams, aguarda no máximo quatro trabalhadores e descarta o registry. Logs
  fixos, sem identificador, token, QR, chave, payload nem exceções do par.
- **C8:** janela obrigatória `--wifi-window-seconds`, entre 1 e 900; não há duração
  padrão que habilite a LAN. Ao expirar ou cancelar, fecha e revoga os pares que
  autenticaram na janela. Falha de gravação deixa a guarda pendente; não há
  sucesso falso. A regra de firewall é removida à mão, mesmo após falha.
- **C9:** sem ADB, instalação, tablet, câmera, Keystore ou mTLS físico.

### L1–L5 da revisão de segurança

- **L1 (identidade e par de teste):** comandos locais explícitos, abaixo. Em vez de
  um pareamento remoto, a chave do par nasce no outro PC (CNG não exportável), só a
  parte pública viaja, e o operador digita no host a impressão digital dessa chave:
  essa é a aprovação local. **Desvio do contrato, para a revisão de segurança decidir:** a
  oferta de bancada (`PanelOSBenchOffer/1`) não é o QR do celular. Ela só tem dados
  públicos (raiz, folha emitida, IP, porta) e **não tem token**, porque a bancada
  não tem porta de pareamento que o consuma. Não há QR de imagem; o app Android
  não consegue ler este arquivo e não deve aceitá-lo.
- **L2 (contadores):** ver abaixo.
- **L3 (recuperação):** `--wifi-bench-recover`, abaixo.
- **L4 (mutex):** com o PanelHost de produção vivo, `--wifi-bench` é recusado pelo
  mutex (`Já existe uma instância do PanelHost em execução`, código de saída 17). A bancada
  exige **parar o PID do processo em uso**, com autorização do mantenedor, e religar o host depois.
  Os comandos de identidade (`init`, `issue-peer`, `revoke`, `recover`) não abrem
  socket nem tocam o cabo e rodam antes do mutex: não exigem parar o host.
- **L5:** registrado acima (mudança de perfil, até cerca de 3 s).

## Comandos locais

Todos rodam não elevados, só no Windows 11, e nunca imprimem chave, token ou
conteúdo de rede. As impressões digitais são hashes de chaves públicas.

```powershell
# 1) Uma vez, no PC do host. Imprime o "host id" (hash SHA-256 da chave pública da raiz).
PanelHost.exe --wifi-bench-init

# 2) Emissão e ativação de um par de teste (a chave pública veio do outro PC).
#    A impressão digital digitada tem de bater com o arquivo; senão nada é emitido.
PanelHost.exe --wifi-bench-issue-peer --wifi-peer-spki device.spki `
  --wifi-peer-fingerprint <64 hex do keygen> --wifi-offer-out offer.json `
  --wifi-bind <IPv4 RFC1918 do host> --wifi-session-port <porta>

# 2b) Só leitura: a interface do IP escolhido já é física, Up e Private? (nunca muda o perfil)
PanelHost.exe --wifi-bench-check --wifi-bind <IPv4 RFC1918 do host>
#    Código 0 = pronta; 3 = não pronta, com o primeiro motivo (não atribuído, tipo, Down, não física,
#    perfil Public/Domain/desconhecido, prefixo). Se o perfil não é Private, o programa recusa; decida com o dono.

# 3) Revogação (por impressão digital); também vale como teste do par revogado.
PanelHost.exe --wifi-bench-revoke --wifi-peer-fingerprint <64 hex>

# 4) Recuperação após saída suja (processo morto): primeiro a prévia, sem mudar nada.
PanelHost.exe --wifi-bench-recover
#    Ela diz se revogaria NENHUM par, UM par ou TODOS. Para aplicar exatamente isso:
PanelHost.exe --wifi-bench-recover --wifi-recover-confirm <none|one|all>
```

Regras: o arquivo da oferta nunca é sobrescrito; a oferta vale 10 minutos (decisão do mantenedor) e só contém
dados públicos; `issue-peer` concede apenas a capacidade de leitura do painel
(`PanelReadCapability=1`); a recuperação recusa enquanto o dono ainda segura a
guarda (pare o processo antes); uma confirmação diferente da prévia não faz nada
(código 4). Códigos de saída: 0 ok, 2 uso incorreto, 4 confirmação necessária,
5 falha.

Recuperação, quem chama e quando: **o operador, localmente, com o processo dono
encerrado**, depois de uma saída suja (a guarda `authorization.pending` ficou e o
próximo `--wifi-bench` recusa começar). Nunca é automática. Se o marcador diz
"revogação interrompida de X" revoga só X; se X já foi compactado e está em
`RevokedKeys`, nada além do já revogado; se o marcador é ilegível ou nomeia chave
desconhecida, revoga todos (fail-closed).

## Contadores de recusa (L2)

No fim da janela o host imprime uma única linha com contagens agregadas por motivo.
Os contadores guardam só números: nenhum IP, identificador, certificado, rótulo ou
payload pode chegar ao log por eles. Exemplo da sequência deste roteiro (é exatamente
o que o teste `Documented_bench_sequence_ends_with_the_documented_counters` exige):

```text
[WiFi bench] Listener and sessions closed.
[WiFi bench] counters: authenticated=3 completed=3 origin_refused=0 admission_refused=4 tls_no_certificate=1 tls_invalid_certificate=1 tls_revoked=1 tls_not_authorized=0 tls_other=7 protocol_refused=0 control_rate=0 command_refused=2 heartbeat_timeout=0 session_revoked=0 pairing_approved=0 pairing_denied=0 pairing_failed=0
```

| Contador | Significa |
|---|---|
| `authenticated` / `completed` | sessões que passaram o mTLS / sessões encerradas |
| `origin_refused` | origem fora da sub-rede ligada (fechada antes de qualquer TLS) |
| `admission_refused` | quinta conexão simultânea, rajada ou 12 por minuto excedidos |
| `tls_no_certificate` / `tls_invalid_certificate` | sem certificado de cliente / cadeia, EKU ou raiz inválidos |
| `tls_revoked` / `tls_not_authorized` | chave na lista de revogados / certificado válido mas sem concessão ativa |
| `tls_other` | TLS 1.2, ALPN errado, handshake abandonado ou estourado (5 s), reset |
| `protocol_refused` | falha de PLMX, hello inválido, capacidade ausente, heartbeat com campos extras |
| `pairing_approved` / `pairing_denied` / `pairing_failed` | convites de pareamento aprovados / recusados ou expirados após o pedido / falhas de prova, formato ou emissão |
| `control_rate` / `command_refused` | limite de Control excedido / Desktop, Stream Deck, toque ou mídia pedidos |
| `heartbeat_timeout` / `session_revoked` | 15 s sem progresso válido / revogação com a sessão aberta |

Leitura da linha de exemplo: `tls_other=7` são TLS 1.2, ALPN errado, slowloris e os
quatro handshakes que o flood deixou pendurados; `admission_refused=4` são as quatro
conexões acima do teto de quatro do flood de oito.

## Cliente de teste (para o outro PC)

`PanelHost.WifiBenchClient.exe` só conecta (nunca escuta). A chave do aparelho de
teste é CNG não exportável, nomeada `PanelOS-WifiBench-<nome>`, e a ferramenta só
apaga chaves com esse prefixo.

```powershell
PanelHost.WifiBenchClient.exe keygen bench1 device.spki        # imprime a impressão digital
PanelHost.WifiBenchClient.exe run offer.json bench1 positive   # mTLS + PLMX + hello + ping + Video FIN
PanelHost.WifiBenchClient.exe run offer.json bench2 revoked    # par revogado (precisa ter sido revogado no host)
PanelHost.WifiBenchClient.exe run offer.json bench1 all-negatives
PanelHost.WifiBenchClient.exe run offer.json bench1 idle       # 15 s de silêncio: o host deve fechar
# A janela inteira, sem digitar nada: positive -> revoked -> all-negatives, com o espaçamento certo.
PanelHost.WifiBenchClient.exe window offer1.json bench1 offer2.json bench2 --expect-host-id <host id do --wifi-bench-init>
PanelHost.WifiBenchClient.exe cleanup bench1                   # apaga a chave CNG
```

Casos: `positive`, `revoked`, `no-cert`, `wrong-root`, `tls12`, `wrong-alpn`,
`desktop`, `stream-deck`, `slowloris`, `flood`, `idle`, `all-negatives` (todos os
negativos, nessa ordem, exceto `idle`). Os casos negativos esperam que o servidor
**feche antes de qualquer byte de aplicação**: em TLS 1.3 o handshake do cliente
pode terminar antes de o servidor recusar o certificado, então "recusado" nunca é
deduzido só da chamada de handshake. A ferramenta espaça os casos em 6 s para não
bater no limite do host (rajada 4, doze por minuto) e espera 60 s antes do `flood`,
que precisa de um minuto móvel limpo. `--ip` só aceita o endereço da oferta ou outro IPv4 RFC1918
(nunca loopback nem IP público); `--port` precisa estar em 1024..65535 e fora de 8765–8767. Opção sem
valor, repetida ou desconhecida é erro (nunca é ignorada em silêncio). Toda execução imprime o **host id**
e a **impressão digital do par** da oferta: compare com o que `--wifi-bench-init` e `keygen` imprimiram,
ou passe `--expect-host-id` para recusar uma oferta de outro host (F1). `window` leva cerca de dois minutos
dos 180 s: inicie-o logo depois de o host dizer que o listener está habilitado.
Não foi testada em outro computador, nem publicada para ele. O PC remoto precisa do
.NET 8 Desktop Runtime e dos binários de `dotnet build` da solução.

## Testes de socket em loopback (só na suíte)

O mantenedor autorizou, **somente na suíte de testes**, um `TcpListener` em `127.0.0.1`
que executa o laço de aceitação real (`WifiBenchListener.ServeLoopAsync`) e o
pipeline mTLS real. Não existe exceção de loopback no produto; o filtro de origem
é um parâmetro do laço e `RunAsync` sempre passa o filtro RFC1918 da sub-rede.
`WifiBenchLoopbackTests` cobre: sessão positiva, sem certificado, outra raiz, par
revogado, TLS 1.2, ALPN errado, Desktop e Stream Deck, slowloris (fechado pelo prazo
de 5 s), quinta conexão (admissão), origem recusada, fim da janela (laço e porta
fechados em ≤ 1 s), revogação com a sessão aberta (≤ 1 s) e a sequência deste
roteiro com a linha exata de contadores. Não prova: filtro de origem real, WMI e perfil
Private, firewall, o fechamento TCP na LAN nem a CLI real.

## Roteiro proposto — ainda não autorizado para execução

Pré-condições (todas precisam de aprovação do mantenedor): rede doméstica, só aparelhos
dele, janela de **no máximo 180 s**, PanelHost não elevado, PID do processo em uso parado e religado
depois, cabo desconectado, sem tablet, ADB nem Keystore. Revisão de segurança antes.

**A. Preparação, sem rede e sem parar o host (podem ser feitas antes):**

1. No outro PC: `keygen bench1 device1.spki` e `keygen bench2 device2.spki`. Anotar as
   duas impressões digitais. Copiar só os `.spki` para o PC do host.
2. No PC do host: `--wifi-bench-init`; anotar o host id (o cliente o repete na janela, ver `--expect-host-id`).
3. Emitir os dois pares (`--wifi-bench-issue-peer`, digitando cada impressão
   digital) com o mesmo IP e a mesma porta aprovados, gerando `offer1.json` e
   `offer2.json`. Revogar o segundo: `--wifi-bench-revoke --wifi-peer-fingerprint <bench2>`.
4. Copiar `offer1.json` e `offer2.json` para o outro PC. São dados públicos.

**B. Antes da janela (somente leitura, depois a regra):**

Atalho, sem digitar os comandos um a um: `scripts\wifi-bench-preflight.ps1 -BindIp <ip> -InterfaceAlias <alias> -Port <porta>`
faz só leitura (nada é criado, mudado ou parado) e junta o hash do exe, `--wifi-bench-check`, o
perfil da interface, as regras já existentes do executável e as regras amplas para a porta, e termina
com `PREFLIGHT PASS` ou `PREFLIGHT FAIL`. Ele **não** cria a regra e não foi executado de ponta a
ponta (só o caminho de erro com executável inexistente e a análise de sintaxe); a primeira execução
real é a do ensaio. Na primeira execução real no PC do host (Ethernet `192.168.15.4`, sub-rede `192.168.15.0/24`), a
checagem 4 acusou 18 regras que **não** abrem a porta: 17 `*-In-Allow-ServerCapability` de apps da Store (têm
`Owner`) e uma `Radmin VPN icmpv4` (protocolo 1, só ICMP). O filtro agora as ignora, e `-SelfTest` (lógica pura, sem
ler o firewall) prova nove exemplos, incluindo esses dois e três regras largas que **devem** continuar acusando:
`powershell -File scripts\wifi-bench-preflight.ps1 -SelfTest`. Os comandos manuais abaixo são os mesmos, para quem preferir conferir um a um.

```powershell
# Valores aprovados. 40002 é proposta, não porta detectada. O CIDR é o da interface escolhida.
$benchIp = '<IPv4 RFC1918 aprovado>'
$benchCidr = '<CIDR exato da sub-rede, ex.: 192.168.1.0/24>'
$benchInterface = '<InterfaceAlias físico aprovado>'
$benchExecutable = '<raiz-do-repositorio>\windows\PanelHost\bin\Debug\net8.0-windows10.0.19041.0\PanelHost.exe'
$benchRuleName = 'PanelOS-Wifi-Bench-Session'
$benchGroup = 'PanelOS-Wifi-Bench'
$benchSessionPort = 40002

# 1) Hash do executável ANTES (bin\Debug é gravável pelo usuário).
(Get-FileHash -Algorithm SHA256 -LiteralPath $benchExecutable).Hash

# 1b) A interface já é Private? (F5) Só leitura; o bench NUNCA muda Public para Private. Se não for Private, parar.
Get-NetConnectionProfile -InterfaceAlias $benchInterface | Format-Table Name, InterfaceAlias, NetworkCategory -AutoSize
& $benchExecutable --wifi-bench-check --wifi-bind $benchIp   # o mesmo critério do listener; código 0 = pronta

# 2) Listar o que já existe para este executável. Se houver QUALQUER regra, parar e decidir com a revisão de segurança.
Get-NetFirewallApplicationFilter -Program $benchExecutable | Get-NetFirewallRule |
  Format-Table Name, DisplayName, Enabled, Direction, Action, Profile -AutoSize

# 3) Regras de entrada Allow ativas, sem filtro de programa, que já abririam esta porta. Se houver, parar.
Get-NetFirewallRule -Direction Inbound -Action Allow -Enabled True | ForEach-Object {
  $app = $_ | Get-NetFirewallApplicationFilter; $port = $_ | Get-NetFirewallPortFilter
  [pscustomobject]@{ Name = $_.Name; Display = $_.DisplayName; Owner = $_.Owner; Profile = $_.Profile; Program = $app.Program; Protocol = $port.Protocol; LocalPort = $port.LocalPort }
} | Where-Object {
  # Ignorar regras de AppContainer (Owner preenchido: apps da Store, "*-In-Allow-ServerCapability") e regras
  # que não são TCP nem Any (ex.: "Radmin VPN icmpv4", protocolo 1): não abrem uma porta TCP para um exe comum.
  -not $_.Owner -and $_.Program -eq 'Any' -and ('TCP', '6', 'Any' -contains "$($_.Protocol)") -and
  ($_.LocalPort -contains 'Any' -or $_.LocalPort -contains "$benchSessionPort")
} | Format-Table -AutoSize
```

Só depois, com elevação **apenas para este comando**, nunca para o PanelHost:

```powershell
New-NetFirewallRule -Name $benchRuleName -DisplayName $benchRuleName -Group $benchGroup `
  -Direction Inbound -Action Allow -Program $benchExecutable -Protocol TCP `
  -LocalAddress $benchIp -LocalPort $benchSessionPort -RemoteAddress $benchCidr `
  -Profile Private -InterfaceAlias $benchInterface -EdgeTraversalPolicy Block
```

Este arquivo **não cria** a regra; o comando acima só existe para revisão. O CIDR exato
substitui `LocalSubnet`, que no Windows cobre as sub-redes de todas as interfaces
locais (VPN e virtuais inclusive).

**C. Janela (≤ 180 s):**

1. Parar o PID do processo em uso (com autorização), desconectar o cabo, confirmar com
   `Get-Process PanelHost` que não resta instância.
2. Terminal não elevado, só na janela aprovada:

   ```powershell
   & $benchExecutable --wifi-bench --wifi-bind $benchIp --wifi-session-port $benchSessionPort --wifi-window-seconds 180 --wifi-cable-disconnected
   ```

3. Se o Windows mostrar o aviso "Permitir acesso" do firewall, clicar em **Cancelar**
   (ele criaria uma regra mais ampla) e encerrar a janela; a regra própria deveria
   tê-lo evitado.
4. No outro PC, **um único comando, sem digitar mais nada**, logo depois de o host dizer
   que o listener está habilitado. Ele roda `positive`, `revoked` e `all-negatives`, nessa
   ordem, com o espaçamento de 6 s e a espera de 60 s antes do `flood` (cerca de 2 minutos
   dos 180 s):

   ```powershell
   PanelHost.WifiBenchClient.exe window offer1.json bench1 offer2.json bench2 --expect-host-id <host id do --wifi-bench-init>
   ```

   Esperado: dez linhas `PASS` e `Summary: 10 PASS, 0 FAIL`. Não repetir casos dentro da
   janela (o limite de 12 admissões por minuto é o desenho); para repetir, outra janela. O caso `idle` (15 s) não cabe sem estourar a janela;
   se for desejado, vira uma segunda janela de 60 s com nova aprovação.

**D. Depois da janela (mesmo após falha):**

```powershell
Remove-NetFirewallRule -Name $benchRuleName
# Conferir que a regra sumiu e que o Windows não criou nenhuma regra automática para o PanelHost.
Get-NetFirewallApplicationFilter -Program $benchExecutable | Get-NetFirewallRule
Get-NetFirewallRule -Group $benchGroup -ErrorAction SilentlyContinue
# 2) Hash do executável DEPOIS; tem de ser igual ao de antes.
(Get-FileHash -Algorithm SHA256 -LiteralPath $benchExecutable).Hash
```

No outro PC: `cleanup bench1` e `cleanup bench2`. No host: religar o PanelHost
de produção (PID do processo em uso), reconectar o cabo e retestar o cabo em etapa autorizada.

**Critérios de sucesso:**

- `positive`, `revoked` e os oito casos de `all-negatives` em PASS; nenhuma resposta a
  comando Desktop ou Stream Deck.
- A linha de contadores do host é igual à de exemplo acima, ou a diferença se
  explica (por exemplo `admission_refused` maior se alguém reconectou rápido).
- Fim limpo: porta fechada e sessões encerradas assim que a janela expirou; os
  pares usados na janela aparecem revogados; sem regra automática para o
  PanelHost; hash do executável idêntico; cabo retestado.
- Se a saída do host foi suja (processo morto), a próxima execução recusa começar;
  use `--wifi-bench-recover` (prévia, depois confirmação).

Isso não homologa vídeo real, câmera, Keystore, recuperação por UI nem lock/sleep.
W15 completo e o uso diário continuam fechados. Não travar nem suspender o PC na
bancada.

## Decisões do mantenedor (F3 e F4)

- **F3 — validade da oferta: 10 minutos, uso único.** A oferta de bancada (`PanelOSBenchOffer/1`) e o
  convite/QR de pareamento (`PanelOSPair/1`, com token) valem 10 minutos a partir da emissão. O convite
  também é de uso único: uma credencial por convite, no máximo cinco tentativas inválidas, aprovação local em
  até 60 s dentro dos 10 minutos. Uma chave já usada ou revogada nunca é reemitida: passando dos 10 minutos,
  é preciso um novo `keygen` (ou novo convite) com outro nome. O desenho original dizia 120 s para o convite;
  os 10 minutos são do dono e vivem em `WifiPairingInvitation.LifetimeSeconds`.
- **F4 — a identidade do PC fica guardada depois do teste.** A identidade (raiz, servidor, chaves CNG não
  exportáveis) permanece em `%LOCALAPPDATA%\PanelOS\Wifi` entre as janelas; não há comando de remoção e ele não
  é necessário agora. Nada abre sozinha: só os pares emitidos e ativados, e a janela revoga os que autenticaram.
## Limitações conhecidas

- Bind, accept real em IP da LAN, WMI/perfil, firewall e fechamento TCP na LAN não
  foram exercitados. Só os tipos UInt32 do schema WMI instalado foram conferidos
  por leitura. O loopback prova o pipeline, não a rede.
- Mudança de perfil de rede leva até cerca de 3 s para fechar a janela (L5).
- `PhysicalAdapter=true` aparece em alguns adaptadores USB ou virtuais (L6): o
  operador confere o adaptador do IP escolhido antes.
- Não há hooks de lock, sleep ou background nesta bancada.
- Morte forçada do processo pode impedir o `finally` (revogação dos pares da
  janela): manter o firewall fechado e recuperar/revogar localmente antes de uma
  nova janela.
- O estado DPAPI tem quota de 48 KiB; o esgotamento bloqueia novas gravações sem
  expulsar hashes revogados.
- Não há cliente LAN/QR no app Android nem UI de aprovação local; a versão atual
  do tablet não consegue executar estes passos.

## Fontes primárias

- [TcpListener, cancelamento e Stop](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.tcplistener?view=net-8.0).
- [PhysicalAdapter](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-networkadapter).
- [Perfis de conexão](https://learn.microsoft.com/en-us/powershell/module/netconnection/get-netconnectionprofile).
- [Firewall e RemoteAddress](https://learn.microsoft.com/en-us/powershell/module/netsecurity/new-netfirewallrule).
- [Timeout de enumeração WMI](https://learn.microsoft.com/en-us/dotnet/api/system.management.managementoptions.timeout).
