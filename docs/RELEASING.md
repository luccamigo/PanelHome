# Como lançar uma versão (release)

Este guia é para quem nunca lançou um release no GitHub.

## O que é um release

Um **release** é a página de downloads do projeto: uma versão com nome (por exemplo `v1.0.0`), notas do que mudou e arquivos para baixar. Aqui os arquivos são:

- `PanelOS-vX.Y.Z-windows.zip`: o programa do Windows (**sem** o instalador do PawnIO);
- `PanelOS-vX.Y.Z.apk`: o app do tablet, assinado;
- `SHA256SUMS.txt`: a "impressão digital" de cada arquivo, para quem baixa conferir que não foi alterado.

Uma **tag** é uma etiqueta no código ("este commit é a versão 1.0.0"). Quando você envia uma tag que começa com `v`, o GitHub roda sozinho o arquivo `.github/workflows/release.yml`: testa, gera os arquivos e cria o release como **rascunho**. Rascunho só você vê; nada vai ao público até você clicar em publicar.

## Preparação única (só na primeira vez)

Decisão tomada: os releases públicos são assinados com uma **chave nova, só sua**. A `debug.keystore` (chave de teste do Android) nunca assina um release público; o workflow recusa o APK se detectar essa chave.

> **Regra de ouro:** a senha da keystore nunca vai para o repositório, para um arquivo do projeto, para o chat nem para o histórico de comandos compartilhado. Digite-a só no terminal, quando o `keytool` pedir, e guarde-a no seu cofre de senhas.

### 1. Gerar a chave (no seu PC, fora da pasta do projeto)

Abra o PowerShell numa pasta **fora** do repositório (por exemplo `C:\Users\SEU_USUARIO\chaves-panelos`) e rode:

```powershell
keytool -genkeypair -v -keystore panelos-release.keystore -alias panelos -keyalg RSA -keysize 4096 -validity 10000
```

O `keytool` (vem com o JDK) pergunta, em ordem: a senha da keystore (escolha uma forte e anote no cofre), nome e organização (pode usar só o seu usuário do GitHub; evite dados pessoais, pois ficam visíveis no certificado do APK) e a senha da chave (pode ser igual à da keystore).

O que você tem agora: o arquivo `panelos-release.keystore`, o alias `panelos` e duas senhas.

### 2. Guardar backup (fora do repositório)

**Se esta keystore ou a senha forem perdidas, nenhum APK futuro conseguirá atualizar o app já instalado**; seria preciso desinstalar tudo de novo.

- Copie `panelos-release.keystore` para pelo menos **dois** lugares seguros fora do projeto (cofre de senhas com anexo, pendrive guardado, disco externo).
- Guarde as senhas e o alias no cofre de senhas, junto da cópia.
- Nunca coloque o arquivo no GitHub, em e-mail ou em chat. O `.gitignore` já bloqueia `*.keystore` e `*.jks`, mas não confie só nisso.

### 3. Cadastrar os 4 segredos no GitHub

Em **Settings → Secrets and variables → Actions → New repository secret**, crie:

| Nome | Conteúdo |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | o arquivo da keystore convertido em texto (base64) |
| `ANDROID_KEYSTORE_PASSWORD` | senha da keystore |
| `ANDROID_KEY_ALIAS` | `panelos` (o alias usado no passo 1) |
| `ANDROID_KEY_PASSWORD` | senha da chave |

Para o primeiro (o resultado vai para a área de transferência; cole no campo do GitHub e depois limpe a área de transferência):

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\Users\SEU_USUARIO\chaves-panelos\panelos-release.keystore")) | Set-Clipboard
```

As senhas você digita direto nos campos do GitHub, copiando do cofre. Depois de salvos, o GitHub nunca mais mostra os valores. Se algum segredo faltar, o workflow falha de propósito e avisa.

### 4. Transição do tablet (uma única vez)

O tablet hoje tem o app assinado com a `debug.keystore`. O Android **não aceita** instalar por cima um APK com outra assinatura. Por isso, no primeiro release público:

1. **Aviso:** ao desinstalar o app, o **pareamento é perdido** e as configurações do app no tablet são apagadas. Você terá de parear de novo com o PC.
2. Anote antes qualquer configuração do app que queira refazer.
3. No tablet, desinstale o PanelOS (Configurações → Apps → PanelOS → Desinstalar).
4. Baixe o APK do release publicado, confira o SHA256 (veja "Conferir um download") e instale.
5. Pareie de novo com o PC.

Isso só acontece agora. Os próximos releases, assinados com a mesma chave nova, atualizam por cima sem desinstalar, desde que o `versionCode` aumente.

## Lançar uma versão, passo a passo

1. **Atualize a versão do app** em `app/build.gradle.kts`: aumente `versionCode` (sempre um número maior que o anterior; sem isso o Android recusa a atualização) e ajuste `versionName`.
2. **Atualize o `CHANGELOG.md`** com o que mudou.
3. Faça commit dessas mudanças e envie para a branch principal (`main`).
4. Confirme que o CI (aba **Actions**) está verde nessa branch.
5. **Crie e envie a tag**. No terminal, dentro do repositório:

   ```powershell
   git tag -a v1.0.0 -m "PanelOS 1.0.0"
   git push origin v1.0.0
   ```

   Use sempre o formato `vNúmero.Número.Número`. Para uma versão de teste use um sufixo, como `v1.1.0-rc1`; ela vira pré-lançamento automaticamente.
6. No GitHub, abra a aba **Actions** e acompanhe a execução **Release**. Leva alguns minutos. Se ficar vermelha, abra a execução e leia o passo que falhou.
7. Quando terminar, vá em **Releases** (barra lateral direita da página inicial do repositório). Aparece um item marcado **Draft** (rascunho).
8. Abra o rascunho, clique no lápis (**Edit**) e confira: os três arquivos anexados (ZIP, APK, `SHA256SUMS.txt`) e as notas. Revise as notas geradas automaticamente (use o modelo de notas do repositório, se houver) e **acrescente o aviso do PawnIO** (o driver não vem no ZIP; instale-o pelo [release oficial 2.2.0](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0)).
9. Clique em **Publish release**. Só neste momento a versão fica pública.

### Se algo deu errado

- **Release ainda é rascunho e está errado:** clique em **Delete** no rascunho, apague a tag e refaça:

  ```powershell
  git push --delete origin v1.0.0
  git tag -d v1.0.0
  ```

  Corrija o problema e crie a tag de novo.
- **Já publicou e há erro:** não troque arquivos de um release público. Lance a próxima versão (`v1.0.1`).

## Conferir um download

Quem baixa pode conferir o arquivo comparando com o `SHA256SUMS.txt`:

```powershell
Get-FileHash .\PanelOS-v1.0.0.apk -Algorithm SHA256
```

O resultado deve ser igual à linha do mesmo arquivo em `SHA256SUMS.txt`.

## O que o workflow faz e não faz

- Roda os testes do Android e do Windows; se falharem, nada é gerado.
- Gera o ZIP do Windows **sem** o instalador do PawnIO e o APK assinado com os segredos.
- Calcula o SHA256 e cria o release **em rascunho**. Não publica sozinho.
- Permissões mínimas: só leitura do código; apenas o último passo recebe permissão de escrever o release. As actions estão fixadas pelo hash do commit (com a versão no comentário).
- O instalador do PawnIO não é baixado nem incluído: o ZIP aponta para o release oficial.

## O que não foi testado

O workflow foi validado só com o `actionlint` (sintaxe e tipos). **Nunca rodou no GitHub.** Pontos que só aparecem na primeira execução: versões das actions e do Java 25 / Android SDK 37 no runner, o caminho do APK não assinado (`app/build/outputs/apk/release/`), o `apksigner` 37.0.0, a necessidade de o ZIP exigir o .NET 8 instalado no PC, e o passo `gh release create`. Faça a primeira execução com uma tag de teste (por exemplo `v0.0.1-rc1`) e apague o rascunho depois.
