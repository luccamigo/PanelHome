# Wi-Fi no Android 9 (API 28)

Decisão do mantenedor, transmitida pela coordenação em 16/11/2026: adaptar para o tablet de teste, Android 9/API 28. O modelo e a versão foram relatados pela coordenação após leitura autorizada;
esta rodada não acessou o tablet. Base: versão anterior do projeto.

## Transporte e identidade

- Wi-Fi passa a exigir API 28+. `minSdk=24` permanece, assim como o caminho USB e suas versões suportadas.
- `org.conscrypt:conscrypt-android:2.9.0` fica empacotado no APK. A versão está fixada no catálogo Gradle;
  era a versão release/latest do [Maven Central](https://repo.maven.apache.org/maven2/org/conscrypt/conscrypt-android/maven-metadata.xml)
  em 16/11/2026. A versão OpenJDK dos testes também é 2.9.0.
- O provider é passado explicitamente a cada `SSLContext.getInstance("TLSv1.3", provider)` do Wi-Fi,
  inclusive no pareamento por código. Não há `Security.addProvider` nem `insertProviderAt`.
- Só TLS 1.3, ALPN `panelos-pair/1` ou `panelos-session/1`, SNI do hostId e a mesma PKI P-256/SHA-256.
  O pin completo, SAN, EKU, validade e raiz privada continuam sendo verificados antes de qualquer segredo.
  Não há TLS 1.2 nem confiança nas CAs do sistema. Mantido o conjunto TLS 1.3 do provider
  (AES-GCM e ChaCha20-Poly1305); o Schannel não mudou.
- ALPN e SNI usam as APIs públicas do Conscrypt para funcionar no API 28. Cada conexão tem contexto novo
  e tickets desabilitados. No host, `AllowTlsResume=false` permanece; sem retomada ou 0-RTT.
- O AndroidKeyStore continua gerando a EC P-256 não exportável, autorizada para SHA-256 e `DIGEST_NONE`.
  A prova de posse usa `SHA256withECDSA`. O TLS delega a assinatura do digest via `NONEwithECDSA` ao Keystore;
  isso foi comprovado no emulador API 28, sem fallback e sem provider global. Não é uma prova do hardware Samsung.
  Trocar essa chamada por `SHA256withECDSA` sobre o digest faria hash duplo; não é um fallback TLS equivalente.
- Câmera: CameraX e a permissão CAMERA já suportam API 28. A permissão de rede local continua apenas no API 37+;
  API 28 não pede localização, SSID ou uma permissão adicional. A câmera real ainda precisa do teste físico.

## Evidência e reprodução

`WifiConscryptTest` confere contexto do Conscrypt, isolamento dos providers, TLS 1.3, SNI e ALPN.
As suítes de pareamento e transporte exercitam o gate em 28 e a recusa em 27, incluindo os negativos de pinning.
`scripts/run-cross-language-wifi-pairing.ps1` usa o cliente Kotlin com Conscrypt e gate 28 contra o host .NET real,
por QR e código, inclusive mTLS/PLMX por 17 s. Os testes JVM usam JNI OpenJDK, não o JNI Android.

`WifiApi28InstrumentedTest` usa o adaptador AndroidWifiIdentity real: gera chave, aprova pareamento de teste,
instala a folha, lê os metadados criptografados, verifica `privateKey.encoded == null`, curva P-256,
assinatura raw ECDSA e mTLS. QR e código passam em loopback IPv4 explícito, com fixtures compartilhadas;
nenhum endpoint de loopback foi acrescentado ao produto. O teste apaga as próprias identidades no `finally`.

Rodar esse teste somente num AVD API 28 selecionado explicitamente. Antes da instalação, conferir
`ro.kernel.qemu=1` e `ro.build.version.sdk=28` pelo serial desse emulador. Não usar
`connectedDebugAndroidTest` com o tablet conectado. Nesta rodada foi usado `emulator-5560`, com servidor ADB
separado na porta 5038 e `--one-device PanelOS-NoPhysicalUsb` para impedir conexão USB física.

```powershell
adb -P 5038 -s emulator-5560 shell am instrument -w -r -e class com.panelos.panelhome.data.wifi.WifiApi28InstrumentedTest com.panelos.panelhome.test/androidx.test.runner.AndroidJUnitRunner
```

O relatório de entrega registra resultados, tamanho e hashes dos binários finais. A homologação do tablet de teste,
o firewall e o pareamento pela LAN dependem da revisão de segurança e da janela autorizada pelo mantenedor.
