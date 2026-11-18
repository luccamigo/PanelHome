# Contribuição

1. Abra uma issue descrevendo o problema ou a mudança antes de um pull request grande.
2. Crie uma branch a partir de `main`.
3. Rode os testes relevantes antes de enviar:
   - `.\gradlew.bat test` (Android)
   - `dotnet test windows\PanelHost.Tests` (Windows)
4. Não inclua segredos, chaves de assinatura (`*.keystore`, `*.jks`), caminhos pessoais
   nem logs com dados do usuário.
5. Descreva no pull request o que mudou e como foi verificado.
