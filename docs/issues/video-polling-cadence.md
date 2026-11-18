# Cadência de vídeo: timeBeginPeriod e limite de 64 Hz

Estado: aberto, sem correção nesta rodada. Relato inicial em 16/11/2026.

`VideoPollingCadenceTests.Poll_loop_reaches_its_design_cadence_while_resolution_is_raised`
falha nos casos 45 e 60: aproximadamente 63–64 Hz contra o piso de 85 Hz.
A revisão independente confirmou a reprodução na base anterior; antecede a adaptação Android 9.

Investigar o escopo/efeito de `timeBeginPeriod`, a resolução efetiva de espera do loop e a
expectativa do teste no Windows atual. Não reduzir o piso nem mudar o agendamento sem medir.
Reproduzir isoladamente e registrar resolução pedida/obtida e distribuição dos intervalos.

Evidência histórica: relatórios internos de validação (API 28), não publicados.
