# Documentação do PanelOS

Este índice separa o comportamento atual do produto, os contratos técnicos e a
evidência histórica. O [README principal](../README.md) é o ponto de partida
para requisitos, desenvolvimento e validação manual.

## Arquitetura e protocolos

- [Arquitetura de transporte](transport-architecture.md): carriers, seleção,
  framing e responsabilidades entre Android e Windows.
- [Autenticação ADB/TCP](adb-tcp-authentication.md): bootstrap, limites de
  confiança e threat model do fallback ADB.
- [PLMX v2](plmx-v2.md): multiplexação, identidade de sessão e reconexão.
- [Limites de recursos](resource-limits.md): tamanhos, filas e políticas de
  descarte.
- [Limitações de dispositivo](transport-device-limitations.md): razão para
  manter ADB/TCP como fallback suportado.
- [Otimização do transporte](transport-optimization.md): decisões e medições do
  caminho USB nativo.

## Runtime e operação

- [Ciclo de vida da sessão Android](android-session-lifecycle.md).
- [Recuperação do Native USB](native-usb-recovery.md): máquina de estados do
  cable pull/replug, ownership do descriptor e runbook da campanha física.
- [Testes de sessão ao vivo](live-session-testing.md).
- [Broker de display privilegiado](privileged-display-broker.md).
- [Diagnóstico dos sensores de CPU](telemetry-cpu-sensors.md).
- [Control Center](../windows/PanelOS.ControlCenter/README.md).
- [GIF e vídeo em loop](loop-media.md): importação no PC, página Mídia e contrato aditivo.
- [PanelDisplayDriver](../driver/PanelDisplayDriver/README.md).
- [Driver WinUSB/AOA](../windows/PanelHost/Transport/Driver/README.md).

## Benchmarks de transporte

Comece pelo [índice dos benchmarks](benchmarks/transport/README.md). A
[metodologia](benchmarks/transport/methodology.md), a
[comparação](benchmarks/transport/comparison.md), o
[registro de gates](benchmarks/transport/gate-ledger.md) e o
[schema](benchmarks/transport/schema.md) descrevem resultados reproduzíveis.

Resultados datados dos benchmarks são evidência histórica, não a
fonte de verdade para defaults atuais. Novos resultados são transitórios e vão
para `.artifacts/benchmarks/transport/`, salvo promoção explícita conforme a
[política de retenção](benchmarks/transport/retention-policy.md).

## Manutenção

- Atualize o documento mais próximo quando mudar um comando, default, contrato
  ou comportamento operacional.
- Mantenha o README principal focado no estado suportado hoje; preserve datas e
  contexto em relatórios históricos.
- Atualize `protocol-fixtures/` e os testes Kotlin/C# quando o formato de uma
  mensagem mudar.
- Não versione saídas de build, logs, dumps, configurações locais de IDE ou
  resultados brutos fora da política de retenção.
