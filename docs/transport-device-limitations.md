# Limitações de dispositivo no transporte

Este documento registra decisões sobre falhas que **não** são regressões do
PanelOS e que, por isso, não bloqueiam a promoção do carrier nativo. Cada
entrada precisa dizer o que foi observado, o que foi eliminado como causa, qual
é a mitigação em código e qual é o comportamento declarado quando a mitigação
não resolve.

## Tablet de teste — a pilha USB trava após cable pull com sessão AOA aberta

**Decidido em 2026-08-26. Estado: limitação de dispositivo, não regressão.**

### O que acontece

No tablet de teste (Android 9, 2019), puxar o cabo enquanto uma sessão AOA está
aberta deixa o `UsbDeviceManager` do Android com a lista de funções vazia. A
partir daí `START_ACCESSORY` é aceito e não faz nada: o tablet nunca mais
re-enumera em modo acessório.

Assinatura no tablet, com o PanelHost parado:

```powershell
& $env:ADB shell dumpsys usb | Select-String 'current_functions'
# current_functions=[        <- vazio: a pilha travou
```

Assinatura no host: `Android accepted START_ACCESSORY but never re-enumerated
into AOA mode within 15 seconds`.

Reproduzido 3 de 3 tentativas. O gatilho é especificamente o pull com a sessão
AOA aberta (modo Desktop ativo).

### O que foi eliminado como causa

adb, o binding do driver WinUSB, um handle de acessório vazado no PanelHome e o
laço de retry do host — todos testados e descartados individualmente. O relatório
detalhado dessa validação não é publicado neste repositório.

A única recuperação conhecida é desligar e ligar o tablet. Um "Reiniciar" da
Samsung pode fazer warm boot sem reinicializar a pilha USB — confirme com
`adb shell cat /proc/uptime`, cujo primeiro valor precisa ser pequeno.
`svc usb setFunctions` não resolve, force-stop do PanelHome não resolve e matar
o adb não resolve.

> **Confirmado em 2026-08-27 contra a máquina de estados nova.** Dois ciclos
> físicos, tablet recém-ligado e `current_functions=ACCESSORY` antes de cada um.
> Nos dois o pull deixou `current_functions=[` e nenhum `ATTACHED` voltou.
>
> No segundo ciclo o app **não encostou no accessory** depois do pull — zero
> `openAccessory=begin`, graças a `DETACH_GRACE_MILLIS` — e a pilha travou
> igual. Isso descarta o app como gatilho: o travamento é do `UsbDeviceManager`
> reagindo ao cable pull, não de nada que o PanelHome faça com o descriptor.
>
> O que mudou é o comportamento do painel enquanto isso: processo vivo, PID
> intacto, descriptor liberado em 22–371 ms, `WAITING_FOR_ACCESSORY` sem timer e
> sem CPU, pronto para reconectar no instante em que um `ATTACHED` chegar. Ver
> [`native-usb-recovery.md`](native-usb-recovery.md).

### Consequência para o Gate I

A campanha de reconexão (Fase 17, mínimo 5/5 ciclos de cabo) **não é executável
neste aparelho**: cada ciclo exigiria um power-cycle do tablet no meio. O Gate I
fica registrado como não executável por limitação de hardware, não como falha do
PanelOS, e não bloqueia a promoção do carrier nativo.

Isto **não** é uma afirmação de que o AOA reconecta corretamente. É uma
afirmação de que este aparelho não permite medir. Se um segundo dispositivo
Android entrar na bancada, a primeira coisa a rodar é a campanha de reconexão
nele — o resultado decide se o defeito é do tablet de teste ou do caminho AOA.

### Mitigação em código — **superada em 2026-08-27**

> **Superseded.** O texto abaixo descreve `MainActivity.observeNativeUsbRecovery`,
> que reiniciava o processo do PanelHome. Esse código não existe mais e
> `NoProcessExitTest` impede que ele volte: o PanelHome é o app HOME do tablet,
> então reiniciar o processo é a tela inicial desaparecendo. A recuperação atual
> é in-process e está em
> [`native-usb-recovery.md`](native-usb-recovery.md).
>
> A afirmação histórica de que **só** um processo novo recuperava o accessory
> continua registrada aqui como evidência do que foi medido em 2026-08-19, mas
> ela foi explicada em 2026-08-27: o que prendia o accessory era uma leitura
> bloqueada em `/dev/usb_accessory` que nem o fechamento do descriptor
> interrompe, e um teardown que ficava na fila atrás dela por 62,8 s. Liberar o
> descriptor sem lock e antes de tudo reduziu esse teardown para 92 ms. Se o
> reboot do tablet continua sendo necessário depois disso é uma pergunta que só a
> campanha física de 5 ciclos responde, e ela ainda **não foi executada** contra
> a nova máquina de estados.

`MainActivity.observeNativeUsbRecovery` reiniciava o processo do PanelHome quando
o carrier nativo ficava fora do ar por 8 s, no máximo 5 tentativas, e somente
enquanto o cabo estivesse alimentando o aparelho. Isso recuperava os casos em que
a transição AOA depende de um processo novo — que é o que acontece quando o host
reemite `START_ACCESSORY` dentro da janela de 15 s.

A mitigação não recuperava a pilha travada descrita acima, e não tentava: um
contador em `SharedPreferences` limitava as tentativas justamente para o painel
não entrar em laço de reinício quando o problema está abaixo do app.

### Fallback declarado

O transporte ADB/TCP **deixa de ser temporário e passa a ser um fallback
suportado**. Quando o carrier nativo não estabelece, a resposta suportada é:

```powershell
.\scripts\start-panel-dev.ps1
```

O launcher prepara o token e o intent com `transport=adb`, que vence a seleção
automática mesmo com um acessório anexado. Os dois lados já implementam isso:
`TransportSelection.shouldUseNativeUsb` trata `transport=adb` como decisão
explícita do operador, e `AdbTcpTransport` continua sendo um backend de primeira
classe atrás de `IPanelTransport` / `PanelTransport`.

Consequências que isso assume:

- o ADB/TCP continua coberto por testes e pelo CI, e não pode ser removido sem
  substituir esta decisão;
- o ADB/TCP autentica cada socket contra processos locais não autorizados com
  um token efêmero de 256 bits. Isso não é segurança geral de rede e não protege
  contra Administrador, `SYSTEM` ou malware em kernel; veja
  [`adb-tcp-authentication.md`](adb-tcp-authentication.md);
- o PLMX só existe no carrier nativo. Uma sessão em ADB/TCP não exercita
  framing multiplexado, resync nem identidade de sessão, então uma regressão de
  PLMX não aparece em uma bancada que rodou só em ADB.

### Como revisitar esta decisão

Revise quando qualquer uma destas mudar:

- um segundo dispositivo Android com AOA entrar na bancada;
- o tablet de teste receber atualização de firmware que altere o `UsbDeviceManager`;
- o carrier nativo passar a falhar de um jeito que a mitigação de reinício não
  cubra.
