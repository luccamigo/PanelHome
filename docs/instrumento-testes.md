# Instrumento: verificação Android

Sem ADB, instalação ou alteração de protocolo nesta etapa. As referências são
DESIGN.md e DECISOES.md da entrega do redesenho. Dados fictícios existem
somente nos testes; o app continua dependendo do computador para receber dados.

## Regressão no computador

Execute na raiz do repositório:

```powershell
.\gradlew.bat testDebugUnitTest lintDebug assembleDebug assembleRelease
```

Os testes ChromeScreenshotTest e InstrumentScreenshotTest renderizam Compose
com Robolectric/Roborazzi, sem emulador ou USB. O padrão verifica as imagens em
app/src/test/screenshots. Uma diferença visual falha o teste. Para atualizar
as referências depois de revisar uma mudança intencional:

```powershell
.\gradlew.bat testDebugUnitTest --tests '*ScreenshotTest' -PpanelRecordScreenshots=true
```

Revise o diff dos PNGs antes de aceitar a nova referência. Para exportar capturas
para uma pasta de evidências, use também -PpanelScreenshotDir=<pasta> ao gravar.
As imagens são 1280×800, quadro de 960×600 dp; a fonte do JVM pode diferir da
Roboto do tablet. Não substituem testes de reprodução, desempenho ou TalkBack.

Cobertura: Painel com/sem faixa tocando, FPS indisponível/sem app/medindo/zero,
temperatura presente/ausente, conexão/desconexão/conectando, Atalhos e estados
de tecla, Computador e reflow, Processos, Avisos vazios/preenchidos/desligados,
recebimento/erro de mídia, HUD limpo/revelado, tempo de ocultação e swipe.

## Roteiro para o teste de UX, após autorização de instalação

1. Abrir o app e verificar que barras do Android não ocupam a tela. Revelar as
   barras com gesto do sistema; sair e voltar ao app. Conferir modo imersivo.
2. Percorrer Painel, Atalhos, Computador, Avisos e Mídia por swipe e pela barra.
   Conferir Processos dentro de Computador e Configurações/Ambiente no ⋮.
3. Reproduzir e pausar uma faixa no PC: conferir cartão grande à esquerda,
   controles e volume; fechar a sessão de mídia: conferir reflow do Painel.
4. Com a integração de telemetria do mantenedor, conferir fonte ausente (sem cartão
   FPS), fonte presente sem app (cartão vazio), medindo (app, taxa e gráfico)
   e zero real. O rótulo apres./s informa taxa de apresentações, não garante
   frames renderizados. Sensor de temperatura ausente não deve mostrar traço.
5. Desconectar/reconectar pelo fluxo autorizado e conferir banner com saída,
   Tentar de novo, Configurações e valores antigos esmaecidos.
6. Publicar valores e avisos pelo Control Center. Conferir lista, detalhes,
   estado vazio e selo em ponto. Não esperar contador nem novos tempos de fila.
7. Publicar GIF/vídeo de teste: Mídia entra limpa; toque revela HUD; outro toque
   ou 4 segundos sem toque esconde. Swipe funciona nos dois estados. Aviso por
   cima não revela HUD. Arquivo inválido mantém Voltar ao painel disponível.
8. Conferir fonte 1,3×, TalkBack, toque e fluidez no tablet de teste. Não encerrar processo
   real nesta etapa; qualquer teste destrutivo precisa de processo fictício e
   autorização da coordenação.

## Limites preservados

Avisos mantém fila, tempos, leitura e retenção atuais: decisões de comportamento
pendentes não foram implementadas a partir da maquete. Sem demo dentro do APK:
a ordem mais recente restringiu dados sintéticos a testes. Sem chip USB porque
o estado de UI não fornece o transporte; sem novo estado/protocolo de bateria.
