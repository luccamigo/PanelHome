# GIF e vídeo em loop no tablet

O Control Center importa um GIF ou MP4 local na página **Mídia em loop**. O Host publica a mídia pelo canal de controle; o tablet a recebe e acrescenta **Mídia** ao final do carrossel. A imagem preenche a área disponível com fundo preto, mantendo a proporção e sem corte. Vídeos ficam sem áudio.

## Usar

1. No Control Center, importe um GIF ou MP4 de até 64 MiB. O formato é verificado pelo conteúdo, não apenas pela extensão.
2. Com Host e tablet conectados e ambos compatíveis, aguarde o recebimento. O progresso aparece na página Mídia do tablet.
3. Navegue até a última página. A mídia repete enquanto essa página está selecionada e o app está em primeiro plano.
4. Substituir a mídia atualiza o tablet sem reiniciar o Host. Remover a mídia remove a página e ajusta o índice do carrossel.

As páginas Computador, Processos, Avisos e Atalhos da fase 1 são preservadas. Só o placeholder de Atalhos sem perfil é omitido quando há mídia. Sem mídia, a navegação mantém o comportamento anterior. Desktop e Configurações continuam fora do carrossel.

Configuração local: `%LOCALAPPDATA%\PanelOS\media.json`; arquivos: `%LOCALAPPDATA%\PanelOS\media\<sha256>.gif` ou `.mp4`. O nome original fica apenas no PC. Nada é salvo no repositório. Um arquivo inválido ou uma falha de importação preserva a mídia anterior.

## Contrato aditivo

`loop_media` deve estar tanto no `hello` quanto no `hello_ack`. Sem negociação ou sem serviço no Host, nenhuma mensagem de mídia é publicada; os recursos anteriores continuam funcionando. A versão do protocolo permanece 1.

| Direção | Mensagem | Conteúdo |
|---|---|---|
| Host → tablet | `loop_media.state` | `available`; quando disponível: `mediaId`, `kind`, `mime`, `sizeBytes` |
| Tablet → Host | `command`, `loop_media.request` | `mediaId`, `offset` |
| Host → tablet | `loop_media.chunk` | `mediaId`, `offset`, `totalBytes`, `data` base64, `last`; ou `error` |

O protocolo não envia nome original, caminho, URL ou configuração do PC. As fixtures compartilhadas ficam em `protocol-fixtures/loop_media/`.

- `mediaId`: SHA-256 hexadecimal minúsculo de 64 caracteres.
- Formatos: `gif`/`image/gif` e `video`/`video/mp4`.
- Arquivo: 1 a 67.108.864 bytes. Cada chunk: no máximo 262.144 bytes, menor que o limite de linha de controle de 1 MiB após base64.
- Pull sequencial: um request por vez, offsets contínuos. Erros: `stale_media`, `invalid_offset`, `unavailable`.
- O Host observa mudanças do arquivo de configuração e publica o estado mais recente em fila limitada. O recebimento de comandos aguarda cada resposta de chunk.

## Cache e reprodução Android

O cache guarda um item em `filesDir/loop-media/`. Downloads usam `.part` e só viram arquivos finais após validar tamanho, SHA-256 e assinatura GIF/MP4. ID ou offset incorreto, chunk vazio, tamanho excessivo, falha de hash ou de escrita deixam um erro visível e não iniciam retries contínuos. Um estado novo ou reconexão permite tentar novamente. Respostas de um download substituído são ignoradas.

Na desconexão, o download parcial é descartado; uma mídia já pronta pode continuar sendo exibida durante a vida do processo. Remover a mídia limpa o cache e retira a página. O GIF usa AnimatedImageDrawable em API 28+ e Movie em API 24–27; o vídeo usa VideoView em loop, sem dependência nova. Ocultar a página pausa inclusive o início pendente antes do prepare; sair da composição libera o player.

## Verificação

Os testes cobrem importação/remoção atômicas, conteúdo inválido, requests/offsets, transferência de 600 KiB, cache/hash, negociação bilateral e peers antigos, fixtures nos dois runtimes, carrossel e pausa do vídeo. Há também harness C# ↔ Kotlin de sessão real em TCP loopback, executado por `scripts/run-cross-language-live-session.ps1`; ele não usa o Host instalado nem USB/ADB.

O porte restaura apenas loop media da `codex/panel-os-consolidado`, adaptado à fase 1. Resultados desta entrega e limites físicos ficam no relatório interno da entrega (não publicado). Resultados históricos da consolidada não provam funcionamento desta versão.

Roteiro físico após autorização/janela: importar GIF claro e escuro e MP4; confirmar carrossel e legibilidade do indicador; navegar para outra página e voltar; sair/retomar app; substituir/remover mídia durante conexão; desconectar/reconectar no meio do download. A API 24–27 exige aparelho ou emulador apropriado. Não iniciar ADB durante a conexão nativa USB: o servidor pode segurar a interface; qualquer uso exige `adb kill-server` ao terminar.
