# Wi-Fi: núcleo e listener de bancada não executado

Este incremento implementa e testa primitivas do perfil `wifi-v1` definido em
o documento interno de segurança Wi-Fi (não publicado). Não é um produto Wi-Fi utilizável:
o listener de sessão de bancada agora tem opt-in explícito, mas não foi executado.
Não há descoberta, firewall automático, tela, câmera, troca automática de carrier
ou pareamento de rede. O cabo continua sendo o caminho do produto. Ver
[roteiro de bancada](wifi-bench.md); revisão de segurança e autorização da janela
precedem qualquer primeira execução.

## Plataformas e base

Base anterior (PAN-4), sem integrar iOS/telemetria nem copiar a master modificada.
Windows verificado: Windows 11 Pro, build 26300. O núcleo de sessão recusa Windows
anterior a 11, habilitação ausente ou processo elevado. Não executa o broker.
Android mantém `minSdk=24`; apenas as APIs Wi-Fi exigem API 28+. Compila com SDK
37 real. O tablet de teste tem Android 9/API 28 (leitura relatada pela coordenação). O Wi-Fi usa
Conscrypt 2.9.0 explícito, sem registro global; ver [adaptação Android 9](wifi-android9.md).
Não assumir TLS/Keystore homologados no tablet.

## Identidade e TLS

`Security/Wifi` no PanelHost usa ECDSA P-256/SHA-256, CertificateRequest e
SslStream/Schannel. CA por conta, pathLen=0, keyCertSign; folhas separadas,
digitalSignature, EKU serverAuth/clientAuth exclusivo, serial positivo de 16 bytes.
Raiz até cinco anos; folhas até um ano e nunca além da raiz. Não instala CA em
repositório do SO. HostId é SHA-256 do DER SPKI completo da raiz; SAN/SNI é
`panelos-<primeiros 32 hex minúsculos de hostId>.local`. A checagem do pin usa os
32 bytes completos, além da cadeia, validade, curva, EKU e SAN.

Delta de contrato aprovado pela coordenação em 09/11/2026: o QR inclui também o DER
público da raiz (`rootCa`), validado contra o hash SPKI completo antes do TLS.
Não se depende de uma raiz enviada pelo handshake e não se provisiona CA no SO.

TLS é exclusivamente 1.3 com ALPN `panelos-pair/1` ou `panelos-session/1`, sem
renegociação/retomada. Cadeias usam confiança customizada e downloads desativados.
O bootstrap usa a raiz pública do QR como âncora em memória, conferida pelo hash
completo. O PC usa `ServerCertificate`, sem `SslStreamCertificateContext.Create`
com cadeia, pois essa API .NET 8 pode gravar certificados no store intermediário.
O caminho final foi provado em memória com Schannel e stores conferidos antes e
depois. Nenhum token é enviado pelo teste antes da validação TLS. Pairing não solicita identidade cliente ainda:
a porta futura não poderá expor PLMX e usará a prova de posse abaixo.

Sessão exige certificado cliente e registro exato de DER/SPKI, inclusive depois
do handshake. Issued só ativa dentro de 60 s; a transição Active é persistida
antes de produzir AuthorizedWifiConnection. Uma sessão por registry; revogação
persiste primeiro e cancela/fecha o stream registrado. Falha de persistência
envenena o registry e cancela sessões. Não há handler remoto que aprove ou revogue.
Ainda é necessário compartilhar a exclusão com o cabo e proteger cada dispatcher
com geração/capacidades; o wrapper sozinho não prova esses requisitos de produto.

O registry é `IDisposable` e adquire `authorization.pending` com flush durável
antes de autorizar. O marcador `owner-v2` distingue uma saída suja de uma
revogação interrompida (`revoke-v2:<KeyId>`), gravada antes de cancelar/persistir.
Depois do commit da revogação, retorna a `owner-v2`. Reinício com marcador exige
recuperação local explícita: o operador, com o processo dono encerrado, roda
`PanelHost.exe --wifi-bench-recover` (ver [docs/wifi-bench.md](wifi-bench.md)), que
chama `WifiIdentityStore.RecoverAuthorizationLocally()` antes de criar o novo registry.
Sem confirmação o comando só mostra o plano (nenhum par, um par ou todos) e não muda
nada. Owner sujo preserva pares; revogação pendente revoga só o KeyId indicado.
Marcador de um par já compactado (o hash está em `RevokedKeys`) é compreendido e
não revoga ninguém além do que já foi revogado. Marcador legado, truncado, inválido
ou com chave desconhecida revoga todos. Sem sucesso ao salvar, o marcador não é removido.
Recuperação nunca ocorre como fallback silencioso de startup. Uma segunda
instância simultânea é recusada; o dono deve ser descartado antes de recuperar.

O limite de oito pares conta somente Active não vencidos e Issued ainda válidos.
Ao emitir em um novo pareamento local, o registry compacta Revoked e expirados.
Não há aceitação implícita por raiz/chave: a allowlist positiva exige o hash exato
do certificado, portanto certificados purgados continuam recusados após reinício.
Somente uma nova chave com emissão/aprovação local pode criar outra concessão;
não existe rota de renovação ou reativação por nome/IP. `RevokedKeys` guarda os
hashes SPKI permanentemente no mesmo estado DPAPI; compactar certificados não
os remove. Recuperação de revogação também registra esses hashes. A quota de
48 KiB de estado continua valendo: se esgotada, novas gravações são recusadas e
o registry fica fail-closed; nunca se expulsa hash antigo para liberar espaço.
Uma futura migração de armazenamento deve preservar todos esses hashes.

Windows: CNG de usuário, chaves nomeadas não exportáveis, DACL da conta; metadados
DPAPI CurrentUser via APIs nativas, diretório de LocalAppData com ACL da conta e
gravação por arquivo temporário/flush/rename. A carga não recria estado corrompido
nem permite wifiEnabled=true nesta fase. Testes criam/apagam apenas suas próprias
chaves/diretórios temporários. Nenhuma privada é exportada para fixtures.

Android: adaptador AndroidKeyStore P-256 por host, até oito identidades; associação
da folha à chave existente por setKeyEntry, sem exportar privada. Metadados AES-GCM
com chave Keystore e AAD ligado ao host, AtomicFile em noBackupFilesDir; ficam fora
de cloud e transferência device-to-device pelas regras dessa pasta do Android.
Chave ou metadado ausente/corrompido não recriam confiança. Hardware backing não é
prometido. TLS usa SSLContext novo por conexão, trust manager estrito, TLS 1.3 e
ALPN. Keystore e socket TLS só foram compilados; não foram executados no tablet.
Os validadores compartilhados de QR, raiz, servidor e folha são executados em JVM
com certificados DER públicos reais e tempo de verificação fixado na fixture.
O QR usa gramática estrita do objeto plano e decodificação de strings por org.json
existente, evitando stub Android ou dependência Robolectric. A entrada de produto
continua verificando API 28+, e Base64/TLS estão anotados para esse requisito.
Raiz exige BasicConstraints crítico; folha exige a extensão BasicConstraints e
KeyUsage exatamente digitalSignature, tanto para servidor quanto para cliente.

## Documento do QR e prova offline

UTF-8 estrito, máximo 1024 bytes, objeto JSON plano com exatamente estes campos
(duplicatas, desconhecidos, tipos/tamanhos errados são recusados):

| Campo | Tipo/valor |
|---|---|
| type | string `PanelOSPair/1` |
| version | inteiro 1 |
| hostId | 32 bytes, Base64url sem padding |
| rootCa | DER público da raiz P-256, até 512 bytes, Base64url sem padding; deve corresponder ao hostId |
| ip | IPv4 RFC1918 literal canônico |
| pairPort, sessionPort | inteiros 1..65535 distintos, fora de 8765..8767 |
| pairId | 16 bytes, Base64url sem padding |
| token | 32 bytes CSPRNG, Base64url sem padding |
| expiresAt | inteiro Unix seconds, informativo; relógio monotônico do host manda |

Aceitar RFC1918 no parser não prova interface física, ausência de VPN, perfil
Private nem origem on-link: essas verificações continuam obrigatórias na admissão
LAN futura. Não há resolução DNS, execução de URL ou conexão dentro do parser.
Renderização/scan do QR e a confirmação visual pertencem à integração de UI.

Transcrição assinada exatamente como no contrato:

`ASCII("PanelOS WiFi Pair v1") || 00 || challenge[32] || hostId[32] || SHA256(SPKI)[32] || pairId[16] || SHA256(token)[32]`.

SPKI DER canônica P-256; assinatura ECDSA/SHA-256 DER X9.62. Janela monotônica
600 s (10 min; decisão do dono, o desenho original dizia 120 s), cinco tentativas inválidas totais, um candidato, desafio por tentativa,
reserva única da chave e aprovação local até 60 s dentro da janela. Cancelar,
expirar ou replay não emitem certificado. A escolha de campos JSON acima apenas
concretiza os dados do contrato; não modifica a transcrição criptográfica.

`WifiPairingWire` limita prefixo uint32 big-endian, JSON UTF-8, mensagem 1..16 KiB,
quatro mensagens/32 KiB por direção, profundidade e nomes duplicados, deadline
de leitura/escrita 5 s. É um codec, sem máquina de mensagens de rede ou dispatcher.

Fixture `protocol-fixtures/wifi/proof-v1.json` contém dados sintéticos e uma
assinatura produzida pela JCA do JDK, verificada em Kotlin/JVM e .NET. A chave
privada foi descartada. Para gerar outra fixture, escolher caminho novo:

```powershell
java scripts/GenerateWifiProofFixture.java <novo-arquivo.json>
```

## Perfil de limites do carrier

`CarrierLimits.Cable/CABLE` continua sendo default. `WifiV1/WIFI_V1` é explícito
nos construtores PLMX, sem entrada do produto que o ative. Perfil Wi-Fi:

- PLMX v2 existente; payload máximo 64 KiB, verificado antes do rent/alocação.
  Fragmentação de saída nunca excede 64 KiB, mesmo com override maior do cabo.
- Somente Control/Video; OPEN duplicado enquanto aberto é recusado. Após FIN/close
  o canal pode reabrir: cliente até 12 e host até 16 aberturas por canal em janela
  monotônica móvel de 60 s. Excesso recebido gera FIN só no canal; Control
  permanece ativo. Retry de vídeo (cabo e Wi-Fi, mesmo `DesktopController`) usa 1, 2, 4,
  8, 16, 30 s (teto) e volta a 1 s depois de uma sessão que recebeu quadros ou que
  durou pelo menos 10 s desde a conexão (N = 10 s: longe do retry de 1 s, perto do
  tempo de um aperto de mão lento). Conexão que nunca entrega quadro não zera o
  backoff, para um par instável não gerar uma OPEN por segundo. Em H.264, só um pacote
  de dados conta como "quadro recebido"; o pacote de configuração (SPS/PPS) não, porque
  um decodificador que nunca recebe imagem não prova que o enlace funciona. O codec
  `auto` do host (`--video-codec auto`, padrão) negocia assim, no cabo: o app anuncia
  H.264 e ZLIB_BGRA8888; o host escolhe **H.264** se o app o anuncia e o encoder H.264
  do host responde à sondagem, senão cai para ZLIB com a linha "H.264 encoder
  unavailable; negotiated ZLIB_BGRA8888 fallback" (`PanelVideoServer.NegotiateAsync`).
  `h264` e `zlib` forçados recusam em vez de cair. Dispose de stream
  antigo não fecha a nova instância. Repetição ativa,
  Assets/Audio, perda de framing ou HELLO pós-handshake encerram a sessão.
- Reservas por direção/canal antes de alocar/enfileirar: Control até 64 itens e
  2 MiB; Video até 32 itens e 2 MiB. Budget agregado até 8 MiB nos dois sentidos.
- Reservas persistem até consumir/enviar/liberar, incluindo o frame atual.
  Wi-Fi não retém buffers em pool: alocação recebida exata evita memória oculta.
  Streams fechados graciosamente com payload ainda legível permanecem no conjunto
  de limpeza até drenar/descartar; falha da sessão também libera essas reservas.
  Reserva adicional conservadora de 80 KiB cobre scratch de saída e staging.
  A fila de saída Video continua com um slot, mais restrita que o teto de 32.
- Estouro encerra o stream, drena buffers e falha pendências; sem descartar
  fragmentos de vídeo e tentar continuar. Escrita Wi-Fi tem teto total de 5 s.

O orçamento é dos payloads possuídos/emprestados pelo carrier mais seu workspace;
não é teto de RAM do processo, TLS, codec ou buffers da aplicação chamadora. As
filas/cotas de aplicação do contrato são adicionais. O perfil de cabo mantém a
política anterior, incluindo filas antigas sem limite; não se destina a LAN.
As mudanças foram exercitadas em sessões PLMX de memória nos dois runtimes,
incluindo consumidor lento, OPEN repetido, cabeçalho excedido e liberação.

## Correções da revisão (A1–A7, DC 1–11)

- Rótulo do aparelho: NFC, 1–64 caracteres, sem controle, formato (inclui
  bidi U+202E), separadores de linha/parágrafo nem substitutos inválidos.
- Tentativas de pareamento abandonadas (Dispose sem prova) contam no limite de 5.
- Janela Issued (60 s) e aprovação local usam relógio monotônico; a janela Issued
  não sobrevive a reinício do processo. Validade absoluta do certificado continua
  pelo relógio de parede.
- `WifiPairingOffer.Decode` expõe um único tipo de erro (`InvalidDataException`).
- Escrita Wi-Fi usa prazo de 5 s por enfileiramento sem CTS por quadro.
- Android usa `java.util.Base64` (API 26+; o código Wi-Fi já exige API 28+).
- Testes: o handshake negativo exige causa `AuthenticationException`; hash exato,
  expiração, cadeia independente do registro, janela de 60 s, sem retomada TLS
  e fila limitada de entrada têm testes que matam os mutantes C3, C4, C5, C10,
  C16 e K3.

Não adotado: nenhuma das correções pedidas foi deixada de lado. Fora do escopo
permanecem os gates acima (listener, firewall, ensaio físico Keystore/mTLS).

## Pareamento no tablet

O pareamento `PanelOSPair/1`, a página Wi-Fi do Control Center, o cliente Android (câmera, código digitado, Keystore, mTLS)
e o roteiro da primeira janela com o tablet estão em [wifi-tablet.md](wifi-tablet.md). Continuam valendo os gates abaixo.

## Gates que permanecem fechados

O listener de sessão `--wifi-bench` é código não executado, desligado por padrão.
Ver [condições e limitações de bancada](wifi-bench.md). Para o produto: integrar UI,
permissões, seleção Private/interface/origem, quatro handshakes/12 por minuto,
limites de comandos, heartbeat, lock/sleep/background, invalidar dispatchers e
ações pendentes, revogação/exclusão com cabo, logs/retenção e ensaio físico de
Keystore/mTLS Android. PIN, descoberta, iOS, firewall e testes LAN ficam fora.
A revisão de segurança e a independente precedem qualquer ensaio de rede.
