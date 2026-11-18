# PanelOS

PanelOS transforma um tablet Android em um painel de controle para um PC Windows.
O tablet mostra telemetria (CPU, GPU, RAM, discos), o player de mídia atual com
controles e um Stream Deck. Um modo Desktop envia a tela do PC ao tablet.

Componentes:

- `app/`: painel Android (PanelHome).
- `windows/`: agente PanelHost (.NET 8) e Control Center para Windows.
- `driver/`: driver de display indireto (PanelDisplayDriver), usado pelo modo Desktop.

A comunicação ocorre pelo cabo USB, por um canal local (loopback) mediado pelo
`adb reverse`. O PanelHost escuta apenas em `127.0.0.1`. Não há descoberta na rede
local nem servidor remoto. Detalhes em
[`docs/transport-architecture.md`](docs/transport-architecture.md) e no modelo de
ameaças em [`docs/adb-tcp-authentication.md`](docs/adb-tcp-authentication.md).

## Requisitos

- Windows 10 ou 11.
- .NET 8 Desktop Runtime (para o Control Center).
- Android com depuração USB autorizada e cabo de dados.
- Para compilar a partir do código: JDK 25 (Gradle), Android SDK Platform 37 e
  Build Tools 37, e .NET SDK 8.0 (fixado em `global.json`).

## Instalação (usuário)

1. Baixe o ZIP do Windows na página de releases.
2. Extraia e execute o Control Center e o PanelHost.
3. Instale o APK do painel no tablet.

## PawnIO e driver (opcional)

- O **pacote público não inclui o instalador do PawnIO**. Alguns sensores de baixo
  nível (por exemplo, tensões e alguns clocks de CPU) dependem dele. Sem ele, esses
  valores aparecem como indisponíveis; o restante funciona.
- Instale o PawnIO pelo release oficial, com aprovação de administrador:
  <https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0>.
- O driver de display (modo Desktop) é um componente separado. Veja
  [`driver/PanelDisplayDriver/README.md`](driver/PanelDisplayDriver/README.md).

## Compilar a partir do código

```powershell
.\gradlew.bat assembleDebug                 # APK
dotnet test windows\PanelHost.Tests         # agente Windows
```

Para empacotar o Windows sem o PawnIO (o pacote público), use o switch `-ExcludePawnIO`:
`.\scripts\package-windows.ps1 -ExcludePawnIO`. Sem o switch, o script inclui o instalador
oficial do PawnIO, que não faz parte deste repositório.

## Limites conhecidos

- Valores de sensores dependem do hardware e do acesso do Windows; ausências aparecem
  como `—`, nunca como zero.
- O controle de mídia depende de o player expor a sessão de mídia do Windows (GSMTC).
- O modo Desktop depende do driver instalado; sem ele, o estado é apenas simulado
  para desenvolvimento.
- O projeto está em fase de testes; não há garantia de funcionamento em todas as
  combinações de hardware.

## Licença

MIT. Veja [LICENSE](LICENSE). Dependências de terceiros e seus avisos estão em
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Contribuição e segurança

- [CONTRIBUTING.md](CONTRIBUTING.md)
- [SECURITY.md](SECURITY.md)
- [CHANGELOG.md](CHANGELOG.md)
