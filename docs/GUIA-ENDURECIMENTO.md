# Guia de endurecimento do ambiente (fora do app)

O SharkBlocker fecha os contornos que **um programa consegue fechar**. Este guia cobre o resto: o que depende do
**computador, da rede e de outra pessoa**. É a parte de maior impacto e de menor esforço de programação.
Leia junto com a seção "Fora do alcance do app" do README (auditoria U8).

> Regra de ouro: cada passo abaixo só funciona se **a chave ficar com outra pessoa** (ou num lugar que você
> não alcança no impulso). Uma senha que você sabe de cabeça é uma porta com a chave na fechadura.

## 1. Windows (PC)

| # | Medida | Como | O que fecha | Cuidado |
|---|---|---|---|---|
| 1 | **Senha de BIOS/UEFI** + boot por USB/CD **desligado** | Entre na BIOS (F2/Del na partida), defina senha de administrador e tire USB/CD da ordem de boot. Ligue o **Secure Boot**. | Live USB e instalação limpa do Windows | A senha da BIOS fica com outra pessoa. Anote o modelo da placa para conseguir resetar em emergência. |
| 2 | **BitLocker** com a chave de recuperação com outra pessoa | Windows Pro: Painel de Controle › Criptografia de Unidade BitLocker. Salve a chave **na conta Microsoft de outra pessoa** ou impressa. | Ler o disco por outro sistema | Não está no Windows Home (use "Criptografia de dispositivo", se houver). Sem a chave, você perde os dados: guarde cópia segura. |
| 3 | **Conta padrão no dia a dia** + conta de administrador com senha que você **não sabe** | Crie uma conta administradora com senha longa e aleatória, entregue a outra pessoa; rebaixe a sua conta para "Padrão" em Configurações › Contas › Família e outros usuários. | Desinstalar, editar `hosts`, mexer em serviços | **Teste antes.** O app pede administrador e o Watchdog reabre o app pela sua sessão: em conta padrão a abertura/elevação pode falhar. Não afirmo que funciona; faça por alguns dias mantendo um caminho de volta (admin conhecido) e confira com `health-check.ps1`. |
| 4 | **Recursos de virtualização desligados** | Painel de Controle › Programas › Ativar ou desativar recursos do Windows: desmarque Hyper-V, Plataforma de Máquina Virtual, Windows Sandbox, Subsistema do Windows para Linux. Desinstale VirtualBox/VMware/WSL. | Navegar de dentro de uma máquina virtual ou do WSL (U8) | Se você precisa de VM para trabalho, deixe e ligue a opção "Máquinas virtuais, WSL e Sandbox" na aba Proteções (encerra as VMs durante o bloqueio). |
| 5 | **Sem navegadores portáteis/outros** | Remova navegadores que você não usa; o app já cobre os principais, mas não cobre todos. | Navegador renomeado ou portátil | Reveja a pasta Downloads e a Área de Trabalho. |
| 6 | **Contas extras e Convidado** | Remova contas que você não usa. | Entrar por outra conta (auditoria U4) | — |
| 7 | **Atualizações automáticas ligadas** | Windows Update normal. | Falhas de segurança | — |

## 2. Rede (cobre todos os aparelhos da casa)

1. Entre no **roteador** e configure o DNS para um filtro de família, por exemplo **Cloudflare Família** (`1.1.1.3` e `1.0.0.3`, bloqueia adulto e malware). Ative também o filtro/controle parental do roteador, se houver.
2. **Troque a senha de administrador do roteador** e entregue a outra pessoa. Sem isso, qualquer aparelho volta ao DNS comum em dois minutos.
3. Bloqueie no roteador as consultas DNS por fora (porta 53) e o DNS-sobre-TLS (porta 853), se o modelo permitir. DNS-sobre-HTTPS por nome o app já trata (lista `dohHosts`).
4. Dados móveis e redes de terceiros **não** passam pelo roteador: por isso o celular tem o filtro próprio (VPN de DNS).

## 3. Celular (Android com o SharkBlocker)

1. Mantenha a **Proteção máxima ligada** depois do período de teste: bloqueia depuração USB e reset de fábrica pelo menu.
2. **Pasta Segura / Segundo espaço / Apps duplos / Espaço Privado:** não crie. O Device Owner exige que não existam na instalação, e depois o app impede novos perfis.
3. **Conta Google:** o reset por recovery apaga o celular, mas o bloqueio de ativação (conta Google) segue pedindo a conta depois. A senha dessa conta **com outra pessoa** torna o reset muito menos atraente.
4. **Senha do SharkBlocker:** anote em papel, em lugar seguro. Não existe recuperação.

## 4. Outros aparelhos (TV, console, tablet, celular de outra pessoa)

O app não alcança nada disso. Use o filtro do roteador (seção 2) e os controles parentais do próprio aparelho, com senha que você não sabe.

## 5. Pessoa de confiança (opcional, mas é o que mais funciona)

- Escolha **uma** pessoa. Combine o papel dela: guardar senhas (BIOS, administrador, roteador, conta Google) e **não entregar no impulso** (regra: só depois de 24 horas e de uma conversa).
- Se o app tiver "parceiro de responsabilidade" (ideia do roteiro), o aviso é só sobre **tentativas de burlar/desinstalar**, nunca sobre o que você pesquisa. Isso você decide; nada disso é obrigatório.

## 6. Limites honestos

- Quem tem acesso físico, tempo e uma chave de recuperação sempre pode contornar. O objetivo é **dar tempo e atrito** até a vontade passar, não ser inviolável.
- Nada aqui substitui apoio humano. Se a vontade for forte demais para um app, procure um profissional de saúde mental: não é fraqueza, é a ferramenta certa.
