# SharkBlocker

Bloqueador de sites, palavras-chave, SafeSearch e programas para Windows 11,
com trava por tempo sem cancelamento, proteção contra desinstalação, hosts
sempre protegido contra edição, pasta protegida contra exclusão, app e
serviço iniciando sozinhos no boot, e proteções permanentes (SafeSearch, DoH
desligado, extensão auto-instalada).

> **Renomeado para SharkBlocker.** O executável agora é `SharkBlocker.exe` e o instalador `SharkBlocker-Setup.exe`.
> De propósito, os nomes internos continuaram os antigos (pasta `ProgramData\SharkBlocker`, serviço
> `SharkBlockerWatchdog`, tarefa `SharkBlockerApp`, pasta de instalação), para que uma atualização não
> perca a senha, as listas e o estado. O ícone do app é o tubarão (`App/Assets/sharkblocker.ico`).
> Artes usadas: a personagem (`bg-character.png`) sobre degradê na janela principal e (`bg-ice.jpg`) na trava, cozinha (`bg-kitchen.jpg`) na trava e na página
> de bloqueio da extensão, "EXIT" (`gauntlet-exit.jpg`) na sequência de confirmações.

## Rodada de 2026-10-29 (PC): Ellen tsundere, rostos corrigidos, trava de 30 min

- **Trava de tela de 30 min** (era 15): constante única `StateManager.ScreenLockPenalty` (compartilhada com o Watchdog); `ScreenLock.PenaltyDuration` aponta para ela. Os textos da trava, da desinstalação e das páginas de bloqueio foram atualizados. (O preset "15 min" de duração de bloqueio manual continua sendo só um atalho de tempo.)
- **Ellen muito tsundere:** dezenas de falas novas (frase do dia, recados espontâneos, noite, "enquanto bloqueado", busca barrada, bloqueio cumprido, frases da trava e resumo semanal). Tom: marra por fora, cuida por dentro, nunca humilha. Nova expressão `shy` (as falas "n-não é como se…").
- **Rostos corrigidos:** 19 figurinhas com contorno rosa contínuo (as de contorno escuro ou translúcido sumiam no fundo escuro) e a imagem de corpo inteiro da trava sem o brilho claro nas bordas. Veja `ELLEN-ASSETS.md`.
- **Sorteio em sacola** (`ShuffleBag`): frases e cenas só se repetem depois de esgotar as opções, e nunca a mesma duas vezes seguidas.
- **Não compilado nem testado aqui** (sem .NET no ambiente de edição): compile e rode `.\health-check.ps1`. Testes que rodam sem .NET: `node tests/extension/run-tests.js` (385 OK) e `node tests/parity/run-parity.js` (128 OK).

## Rodada de 2026-11-01 (PC): botão de urgência

- **"Preciso de uma pausa":** item na bandeja e botão na aba de bloqueio. Trava a tela por 30 min **sem senha** (só reforça a proteção), depois de uma confirmação. Motivo `urgency`: **não conta como tentativa de burlar** (a sequência não zera) e a trava usa a cena suave (sem a Ellen brava), com o aviso do CVV 188.
- **Correção:** o teto de segurança `ScreenLock.MaxDuration` era 20 min e cortava a trava de 30; agora é `ScreenLockPenalty` + 15 min.
- Removida uma frase duplicada da frase do dia.
- **Não compilado** (sem .NET no ambiente de edição). Teste rápido sem esperar: `SharkBlocker.exe --test-lock 20` para a trava; o botão de urgência usa os 30 min reais.

## Rodada de 2026-11-01 (2) (PC): dias limpos

- Aba Status: nova linha **"Dias limpos (últimos 30)"** (ex.: 27 de 30, 90%). Tentar burlar zera a sequência **atual**, mas só "suja" aquele dia; o recorde e o restante da janela permanecem. Quem começou há pouco conta só os dias desde o início.
- O resumo semanal da Ellen cita os dias limpos quando houve tropeço.
- Testes: `dotnet test tests/SharkBlocker.ParityTests` (inclui `StatsTests`). **Não compilado** ainda.

## Rodada de 2026-11-01 (3) (PC): máquinas virtuais, WSL e Sandbox

- Nova opção (aba Proteções, **desligada por padrão**): durante o bloqueio encerra WSL, Windows Sandbox, VirtualBox, VMware e QEMU (inclusive emuladores Android). Uma VM tem rede própria e escapa de hosts, políticas e extensão (auditoria U8). Ligar não pede senha; desligar pede senha, trava de 30 min e confirmações.
- Limite: o Windows pode recusar encerrar o `vmmem` (fica no log); os programas que abrem a VM sempre caem. VM já aberta antes do bloqueio é encerrada em ~3-5 s.
- **Não compilado** ainda.

## Rodada de 2026-11-01 (4) (PC): firewall contra VPN, proxy e Tor

- Durante o bloqueio, o Watchdog cria regras do Firewall do Windows que **bloqueiam a saída de rede** dos programas de VPN/proxy/Tor que o bloqueador encerra e das VPNs conhecidas instaladas (NordVPN, ProtonVPN, Mullvad, WireGuard, OpenVPN, ExpressVPN, Surfshark, CyberGhost, Windscribe, Psiphon, TunnelBear, Hotspot Shield, ZenMate, PureVPN, UltraSurf, Cloudflare WARP). Fecha a brecha dos poucos segundos entre o programa abrir e ser encerrado, e de serviços que reiniciam sozinhos.
- As regras só valem durante o bloqueio e saem quando ele acaba, na desinstalação e no `emergency-fix-hosts.bat`. Nomes: `SharkBlocker-FW-...`. Para conferir: `netsh advfirewall firewall show rule name=all | findstr SharkBlocker-FW`.
- Nunca bloqueia a internet em geral, nem programas da pasta do Windows. Sem efeito se o Firewall do Windows estiver desligado.
- **Não compilado nem testado** ainda.

## Rodada de 2026-11-01 (5) (PC): filtro de DNS local (avançado, opcional)

- Aba Proteções, **desligado por padrão**. O Watchdog responde o DNS do PC em 127.0.0.1:53: sites da sua lista (com todos os subdomínios) deixam de existir; o resto vai ao Cloudflare Família (que também recusa sites adultos e malware). Os nomes `use-application-dns.net` e `mask*.icloud.com` recebem recusa, o que desliga o DNS-sobre-HTTPS automático do Firefox e o Relay Privado do iCloud.
- Firewall: bloqueia DNS-sobre-TLS (porta 853), DNS-sobre-HTTPS para os resolvedores públicos conhecidos (porta 443) e DNS comum (porta 53) para qualquer servidor que não seja o filtro ou o Cloudflare Família.
- **Segurança de reversão:** só troca o DNS depois de um autoteste que passa; se o autoteste falhar 2 vezes seguidas, desfaz tudo sozinho (prefere ter internet a ter o filtro). Também desfaz ao desligar a opção, quando o bloqueio acaba (sem proteções permanentes), ao parar o serviço, na desinstalação e no `emergency-fix-hosts.bat`. `.\health-check.ps1` (seção 7) confere.
- **Limites:** se o Watchdog for morto à força, o DNS fica em 127.0.0.1 até o serviço reiniciar (ou o emergency-fix); DoH para um servidor fora da lista não é pego; VPN e VM têm DNS próprio.
- **Não compilado nem testado no Windows.**

## Rodada de 2026-11-01 (6) (PC): mapa de horários de risco

- Aba Status: barras por hora do dia (0h a 23h) com as buscas barradas e tentativas de burlar do histórico guardado (até 60 dias). Com pelo menos 5 ocorrências concentradas, sugere a janela de horário e oferece **"Criar agenda sugerida"**: o editor de agenda abre já preenchido; só vale se você salvar, e a mudança passa pela mesma trava de 30 min e senha de qualquer alteração de agenda.
- Sem dados suficientes (menos de 5) ou com ocorrências espalhadas, não sugere nada. Se o horário já está coberto pela agenda, avisa em vez de sugerir de novo.
- **Não compilado** ainda. Testes: `dotnet test tests/SharkBlocker.ParityTests`.

## Interface (tema "Ellen Joe" v2)
- **Tema** em `App/App.xaml`: dark mode grafite com carmesim como destaque. As chaves de cor antigas foram
  mantidas (`ColorDanger`, `ColorSurface`...), então o código que usa `FindResource` continua igual.
  Novidades: tipografia (`TitleLarge`, `SectionHeader`, `Overline`, `HelpText`), botões com hover/clique
  animados (`PrimaryButton`, `SecondaryButton`, `ChipButton`, `GhostButton`), abas com sublinhado animado,
  campos com anel de foco, barra de progresso arredondada e estilos de janela de diálogo (`DialogShell`).
- **Animações** (`App/Services/UiMotion.cs`): entrada da janela, troca de aba, aviso flutuante, diálogos e tela de
  bloqueio. Respeitam "Mostrar animações" do Windows: desligado = nada se mexe.
- **Frase do Dia** (`App/Controls/EllenQuoteCard` + `App/Services/EllenQuoteService.cs`): card com a Ellen e balão
  de fala, mostrado no **widget do canto inferior esquerdo da tela** (`App/Dialogs/DesktopQuoteWindow`; fica por
  cima das janelas sem roubar o foco). Uma frase por dia (a mesma o dia todo, troca à meia-noite, sem repetir até
  acabar o ciclo). Para uma frase nova a cada abertura, mude `NewQuoteOnEveryLaunch` para `true`. Botão
  "Outra frase" para ver mais uma. As frases e a expressão (`calm`, `determined`, `sweet`, `warm`, `smirk`)
  ficam na lista do próprio `EllenQuoteService.cs`.
- **Falas espontâneas** (`App/Services/EllenChatterService.cs`): a cada 25 a 45 min a Ellen "puxa assunto" no
  widget por alguns segundos (a primeira, uns 8 a 15 min depois de o app abrir) e o card volta para a frase do dia.
- **Modo Silêncio** (`App/Services/EllenSilenceService.cs`): por **1 hora** o widget some e a Ellen não puxa assunto.
  Liga pelo botão da lua no widget ou por "Modo Silêncio da Ellen (1 hora)" no menu da bandeja; o mesmo item
  vira "Encerrar o Modo Silêncio (faltam N min)" enquanto ativo, e "Mostrar/esconder a frase na tela" também
  o encerra. Passada a hora, o widget volta sozinho (se estava na tela) e as falas recomeçam daqui a alguns
  minutos. O fim fica em `ProgramData\SharkBlocker\ellen-silence.txt`, então reabrir o app não zera a hora. Só
  afeta o enfeite da Ellen: bloqueios, trava de tela e proteções seguem iguais.
- **Novas imagens** (expressões da Ellen etc.): ver `ELLEN-ASSETS.md`. Sem elas o app usa a imagem atual.

## Fechar o app à força agora trava a tela
- **O Watchdog vigia o app a cada segundo.** Se o processo some sem a marca de "saída normal"
  (Gerenciador de Tarefas, `taskkill` etc.), ele trava a tela por 30 min e reabre o app (que desenha a trava).
  Também cobre matar app e serviço juntos (o app grava um batimento; o serviço que reinicia confere).
- **O X só minimiza.** O menu "Sair" da bandeja agora é uma tentativa de fechar: trava de 30 min + senha +
  confirmações. Durante um bloqueio temporizado não existe saída (só a trava).
- **O app vigia o serviço.** Se o Watchdog some por 15 s (com bloqueio ou proteções ligados), trava a tela
  e inicia o serviço de novo. O serviço também recria a tarefa de abertura automática se alguém a apagar.
- **Pasta de instalação protegida SEMPRE** (antes só com as proteções permanentes), reaplicada a cada ~60 s.
  Se a proteção sumir depois de aplicada (alguém rodou `icacls`/`takeown`), trava a tela por 30 min e reaplica.
- Saída normal (fim de sessão do Windows, falha interna do app, desinstalação) não é punida.
- **Correção (matar app + Watchdog no Gerenciador de Tarefas não travava):**
  1. O Guard do app só agia após 15 s sem o Watchdog, mas o Windows reinicia o serviço em 5 s — então
     matar só o Watchdog nunca era percebido. Agora o Guard compara o **PID** do serviço (vem no pipe de
     status): PID diferente = o anterior foi morto = trava. Processo sumido espera 3 s (antes 15 s);
     processo de pé mas mudo continua em 15 s. `Arm()` esquece o PID (reinstalar o serviço de propósito não pune).
  2. **Rede de segurança no próprio app** (`App.xaml.cs`): ao abrir, se a execução anterior deixou um
     batimento recente (≤ 10 min, desde o boot) sem marca de saída normal e o Watchdog não tratou
     (ele apaga o batimento ao punir), o app grava a trava de 30 min na hora. Não depende do serviço voltar.
  3. `Installer.iss` e `protection-pause.ps1` apagam `app-alive.flag` ao encerrar o app de propósito
     (atualização), para uma atualização legítima não virar punição.
  4. **Reação imediata do Watchdog:** ao iniciar, a PRIMEIRA coisa que ele faz (antes de WMI/servidor local)
     é conferir `watchdog-running.flag` (gravado ao subir, apagado na parada normal). Se sobrou, a instância
     anterior foi morta: trava a tela na hora e o monitor já começa a reabrir o app. O reinício automático
     do serviço caiu de 5 s para 1 s (`sc failure`, reaplicado a cada ~60 s em instalações antigas).
  5. **`quick-install.ps1` transacional:** diario em `C:\ProgramData\SharkBlocker\install-journal.json`
     (etapas Started > Paused > Staged > BackedUp > Copying > Copied > Validated > Complete), copia para
     `<Destino>.staging` e valida (existencia, tamanho, SHA-256, JSON valido) antes de tocar a instalacao,
     backup em `<Destino>.backup` com rollback se a copia/validacao falhar, e `finally` que SEMPRE retoma
     as protecoes. Se for interrompido, a tarefa `SharkBlockerInstallRecovery` (SYSTEM, no boot) ou a proxima
     execucao conclui a copia a partir do preparo integro ou restaura o backup. Para forcar a verificacao:
     `.\quick-install.ps1 -RecoverOnly` (log em `ProgramData\SharkBlocker\install-recovery-log.txt`).
     No fim valida os arquivos instalados e confere se o servico aponta para o Watchdog novo.
  6. **Reinicio planejado (sem trava apos instalar):** `quick-install.ps1` e `protection-pause.ps1` gravam
     `C:\ProgramData\SharkBlocker\planned-restart.flag` (data/hora UTC) ao pausar, ao retomar e ao abrir o app.
     Enquanto o marcador tem menos de 10 min, Watchdog, Guard e app reabrem o app mas NAO travam a tela por
     causa do encerramento feito pela instalacao. Limite honesto: quem tem Administrador pode gravar esse
     arquivo na mao; ele vale so 10 min e a pausa normal ja exige a senha do SharkBlocker e e recusada durante um bloqueio (ver 1e).
  7. **Blindagem dos arquivos de configuracao/dados** (`App/Services/AtomicFile.cs`):
     - Leitura com `FileShare.Read | Delete` e escrita em temporario exclusivo -> `Flush(true)` -> validacao -> troca
       atomica, com novas tentativas em conflito de compartilhamento (antivirus, backup, o outro processo). O
       `Logger` e os marcadores de presenca (`app-alive.flag` etc.) usam o mesmo padrao.
     - Backup automatico do `state.json`: `state.json.bak` (so recebe a versao atual se ela for VALIDA; antes o
       `File.Replace` copiava ate um arquivo corrompido por cima da ultima copia boa) + instantaneos datados em
       `ProgramData\SharkBlocker\backups\` (no maximo 10, 1 a cada 10 min).
     - Auto-reparo: se o `state.json` sumir ou ficar ilegivel, o `Load()` restaura sozinho da copia valida mais
       recente e o arquivo ruim vai para `state.json.corrupt-*` (ate 3). Log: "restaurado automaticamente de ...".
     - Permissoes da pasta de dados: a pasta continua em `C:\ProgramData\SharkBlocker` (e NAO em AppData) porque o
       Watchdog roda como SYSTEM e o app como Administrador e precisam do MESMO estado; AppData e por usuario.
       Agora a pasta nao herda as regras de ProgramData: SYSTEM e Administradores = controle total, Usuarios =
       somente leitura. Confira com `icacls C:\ProgramData\SharkBlocker`.
  8. **Senha obrigatoria e servico nativo** (detalhes em `ARCHITECTURE.md`): fechar o app, remover itens,
     desativar protecoes e desinstalar passam por `PasswordGate` - sem senha definida a acao e RECUSADA, erros
     seguidos geram espera crescente (30 s a 30 min, contador assinado no Registro), PBKDF2 com 600.000
     iteracoes, hash espelhado e assinado no Registro. Novos scripts: `install-service.ps1` (Watchdog como
     servico nativo), `uninstall-service.ps1` (passa pela senha do app) e `health-check.ps1` (diagnostico).
  9. O Watchdog agora loga ao iniciar o monitor: app rodando, batimento recente e saída normal recente.
     Se ainda falhar, olhe `C:\ProgramData\SharkBlocker\app.log` (linhas "MonitorApp", "ENCERRADO À FORÇA", "VOLTOU COM OUTRO PID").
- Limite honesto: um administrador que pare o serviço E o app ao mesmo tempo com `sc config`/`takeown` num
  script único ainda consegue; o objetivo aqui é tornar o caminho óbvio (Gerenciador de Tarefas) impraticável.

## Remoção protegida e anti-ofuscação
- **Remover = trava de 30 min.** Tirar (ou trocar) qualquer item já salvo nas listas de sites, palavras,
  programas ou caminhos, remover/substituir a lista importada ou desativar as proteções permanentes dispara a
  trava de tela de 30 min NA HORA, e só prossegue com senha (se houver) e a sequência de confirmações. Se não
  passar, as caixas voltam ao que estava salvo. Implementado em `UninstallFlow.TryAuthorizeRemoval`.
- **Termos disfarçados:** `n4k3d`, `p0rn`, `n@ked`, `p.o.r.n`, `p o r n`, `pooorn`, letras russas/gregas
  parecidas, largura total (`ｐｏｒｎ`) e caracteres invisíveis agora são pegos, tanto nos termos embutidos
  quanto nas palavras da sua lista (que também são "limpas": `p0ker` na lista vale `poker`).
  Código: `BrowserExtension/deobf.js` e o espelho `App/Services/Deobfuscator.cs` (mude os dois juntos).
  Dígitos só viram letras em palavras que MISTURAM letras e dígitos, então "455" ou "h264" não disparam.
  Limite conhecido: letras trocadas por símbolos sem relação ("p*rn") e texto dentro de imagens não são pegos.
- **Fundo da janela principal:** arte de corpo inteiro da Ellen recortada (a tela de trava usa outras artes).
  Assinatura "Ellen is watching" no cabeçalho, na tela de trava, na dica da bandeja e na página de bloqueio.

## Ícone profissional, saúde e desempenho
- **Ícone refeito** sem contorno rosa: tubarão branco sobre placa escura em degradê. O `.ico` tem 9 tamanhos
  (16 a 256, incluindo 20/24/40 usados em telas com escala de 125%/150%), cada um desenhado no seu tamanho,
  para ficar nítido na barra de tarefas. A extensão do navegador também ganhou ícones (16/48/128).
- **Saúde das proteções:** a cada 60 s o app confere o serviço, a abertura automática e (com as proteções
  permanentes ligadas e o navegador aberto) o sinal da extensão. Aparece no cabeçalho; se piorar, avisa
  na tela e na bandeja.
- **Desempenho:** `StateManager.Load()` agora guarda o último resultado e só reanalisa o `state.json`
  quando o arquivo muda (antes lia e desserializava tudo várias vezes por segundo, o que pesava com
  listas importadas grandes). Regra nova: nunca alterar uma lista do estado por dentro (`.Add`/`.Remove`);
  sempre atribuir uma lista nova.
- **Backup das listas:** exportar/importar sites, palavras, programas e caminhos em `.json`.

## Rodada mais recente
- **Ícone:** tubarão branco (recortado do logo enviado) em `App/Assets/sharkblocker.ico` / `shark.png`.
  O logo original é uma prévia da Shutterstock: para uso pessoal tudo bem, mas para distribuir o app
  é preciso licença da imagem (ou trocar o ícone).
- **Bug corrigido (bloqueio de URLs coladas):** só `http(s)://` e a `/` final eram removidos. Uma URL com
  caminho (`youtube.com/watch?v=x`) gerava linha inválida no hosts e NADA era bloqueado; `www.site.com`
  virava `www.www.site.com` (o site sem www ficava livre). Agora `HostsFileManager.NormalizeDomain`
  reduz tudo ao domínio (`youtube.com`), aceita maiúsculas, porta, `www.` e domínios com acento (punycode).
- **Validação antes de travar:** linhas que não são domínio impedem iniciar o bloqueio (que não tem volta),
  e lista inteira vazia pede confirmação.
- **Contadores** nos títulos das listas e **tempo restante** na dica do ícone da bandeja.
- **Janela principal:** cabeçalho com altura fixa (a arte dentro dele esticava a janela na vertical).
  A arte da Ellen agora é o fundo da janela.

## Tema "Ellen Joe" + revisão de bugs (rodada mais recente)

**Visual.** Todo o app usa a paleta da mascote (preto, carmesim, rosa-choque, azul-gelo): janela principal
com cabeçalho e mascote, abas e botões novos, caixas de mensagem, diálogo de senha, sequência de
confirmações e tela de trava (tubarão 🦈, barra de progresso, mascote balançando) e a página de bloqueio
da extensão. As cores ficam em `App/App.xaml` (chaves `Color*`); troque lá para mudar o tema inteiro.
A imagem é a de sempre: `App/Assets/lockscreen-mascot.png` (e a cópia `BrowserExtension/mascot.png`).
`Dialogs/ThemedMessageBox.cs` substitui o `MessageBox` do Windows com o mesmo uso.

**Mais fácil de usar.** Atalhos de tempo (15 min, 30 min, 1 h, 2 h, 4 h) com "= 1 hora e 30 minutos"
ao digitar; aviso extra para bloqueio acima de 24 h; avisos rápidos no rodapé ("Listas salvas.");
sequência de 100 confirmações numa janela só, com contador e barra (Enter/Esc = desistir);
abre direto na bandeja no login (`--minimized`); abrir o programa de novo traz a janela da bandeja;
ícone da bandeja é a mascote; importação aceita formato hosts com comentários e listas de um domínio por linha.

**Bugs corrigidos.**
- `ProcessBlocker` encerrava durante o bloqueio qualquer programa com "temp" ou "appdata\local" no
  caminho (Chrome instalado por usuário, Discord, Teams, OneDrive, o desinstalador...), além de
  `msiexec`, `thunderbird` e antivírus. Lista enxugada, e agora há uma lista de processos que nunca são
  encerrados (o próprio app, Watchdog, explorer, svchost...). Firefox/Opera/Vivaldi **continuam**
  sendo encerrados no bloqueio (a extensão não cobre esses navegadores); tire de `BlockedProcessNames`
  se não quiser isso.
- `state.json` corrompido zerava tudo, **inclusive a senha**. Agora a gravação é atômica e há `state.json.bak`.
- Log (`app.log`) crescia sem limite; agora gira em 2 MB.
- Contagem do bloqueio mostrava só as horas do dia (2 dias apareciam como "00:..").
- Mensagens da faixa de status eram apagadas a cada segundo pelo timer.
- A janela cancelava o fechamento do Windows ao desligar/sair da conta (agora respeita `SessionEnding`).
- App travava ao abrir e durante o bloqueio (serviço/`sc`/`schtasks`/varredura de processos na thread da
  interface); agora roda em segundo plano.
- `Application.MainWindow` apontava para a janela da trava (já fechada) quando ela abria antes da principal.
- Diálogo de senha não focava o campo; cancelar a "nova senha" caía em "Senha não pode ser vazia".
- Desinstalação: se uma etapa de limpeza falhava, o resto era abandonado; agora cada etapa é isolada e as falhas são listadas.
- Segunda instância não fazia nada (parecia quebrado); agora mostra a janela da primeira.

> **Não consegui compilar aqui** (sem .NET SDK). Os XAML foram checados como XML bem-formado e os testes da
> extensão passam (112 ok), mas rode `dotnet build -c Release` e me mande qualquer erro.

## Novidades desta versão

**1. Desinstalar trava a tela por 30 minutos antes da decisão final — TODA vez.**
Ao clicar em Desinstalar (ou ao rodar o desinstalador do Windows, que chama
`SharkBlocker.exe --uninstall`), TODAS as telas ficam pretas com uma contagem
regressiva de 15:00. Só depois vêm a senha e a sequência de confirmações.
- Teclado: um hook global engole todas as teclas. O Gerenciador de Tarefas fica
  desativado durante a trava (e é restaurado ao fim e em toda abertura do app).
- O fim é por horário gravado e assinado: fechar o app ou reiniciar o PC **não zera** a
  espera; ao reabrir, a trava volta só pelo tempo que faltava.
- **Limite que não dá pra contornar por software:** Ctrl+Alt+Del não pode ser
  bloqueado por nenhum programa. Ele ainda abre "Sair / Reiniciar / Desligar". Sair ou
  reiniciar não zera a espera; desligar no botão sempre funciona (é a válvula de
  segurança caso algo dê errado). Teto de segurança: a trava nunca passa de 20 min.
- Pra testar sem esperar 30 minutos: `SharkBlocker.exe --test-lock 20`.


**1b. Busca/página proibida trava a tela por 30 minutos.**
Quando algo proibido é detectado, o serviço grava a trava e o app a exibe em até 1 s (mesma tela
da desinstalação). Se o app estiver fechado, o Watchdog pede ao Agendador de Tarefas para abri-lo.
Uma trava já em andamento não é estendida nem reiniciada.
O que trava: **conteúdo adulto** (`BrowserExtension/adult-terms.js`), **proxy/VPN/navegador
remoto** (`proxy-terms.js`) e **termo da SUA lista digitado numa busca**. Vale com as proteções
permanentes ligadas **ou** com o bloqueio ativo.
O que NÃO trava (só mostra a página de bloqueio): site comum da sua lista, termo que aparece só
no meio de uma URL ou no texto da página. Motivo: palavra curta na lista (ex.: "tor") casaria no
meio de qualquer URL e travaria o PC sem motivo. Para mudar, ajuste `LOCK_REASONS` no
`background.js` e `LockReasons` em `UrlPolicy.cs`.
A tela de trava mostra uma frase motivacional sorteada (lista em `Dialogs/MotivationalQuotes.cs`) e a
imagem `Assets/lockscreen-mascot.png` no canto inferior esquerdo (troque o arquivo para mudar).

**1c. Proxies, VPNs e navegadores remotos (browser.lol e similares).**
A extensão barra proxies web, VPNs e "navegadores na nuvem" pelo **nome do site** e pelo
**texto da busca**, e também olha o **destino escondido** dentro de outra URL
(`?url=...`, base64, arquivo do Wayback, `*.translate.goog`): um proxy desconhecido não serve
de atalho pra um site que você bloqueou. Vale com o bloqueio ativo **ou** com as proteções
permanentes ligadas (senão um proxy anularia o filtro adulto o tempo todo).
- Os dados ficam em `BrowserExtension/proxy-terms.js` (domínios conhecidos, termos, exceções) e a
  lógica em `BrowserExtension/matcher.js`. Edite o primeiro; recarregue a extensão.
- **Palavras coladas** ("proxybrowser", "freeproxysite", "pr0xy", "p r o x y") são detectadas.
  Para isso não virar bloqueio em tudo, os termos têm camadas: *strong* (trecho de qualquer
  palavra, só sequências longas como "proxy"/"vpn"), *words* (palavra inteira, ou colada só a
  "companions" como "unblocked"+"games"), *hostOnlyWords* (só no nome do site, não em buscas) e
  *safePhrases* ("reverse proxy", "proxy design pattern" passam). O **caminho** da URL não é
  checado, então artigos e documentação sobre proxy continuam abrindo.
- Se algo legítimo for barrado (ex.: o portal de VPN do trabalho), acrescente o domínio em
  `allowDomains`. Se um proxy novo escapar, acrescente em `domains`.
- Limite honesto: um proxy novo **sem nenhuma pista no nome** e sem aparecer na lista passa; no
  navegador remoto (browser.lol) a extensão só enxerga a entrada, nunca o que roda lá dentro.
- Testes: `node tests/extension/run-tests.js` (não precisa de navegador).

**1d. Proteção em nível de DESKTOP (não depende da extensão).**
Remover a extensão não desliga mais o bloqueio, por três camadas independentes:
- **Vigia de barra de endereço** (`BrowserUrlWatcher.cs`, no app): a cada ~1,5 s lê a URL do
  navegador em primeiro plano (Chrome, Edge, Brave, Opera, Vivaldi, Firefox) por UI Automation e
  aplica as mesmas regras da extensão (`UrlPolicy.cs`, porte do `background.js`, lendo os mesmos
  arquivos `adult-terms.js` e `proxy-terms.js`). Ao detectar: trava a tela e fecha a janela do
  navegador (senão a página ainda estaria lá quando a trava acabasse).
- **Arquivo hosts**: os domínios da lista `domains` do `proxy-terms.js` entram no hosts com as
  proteções permanentes, valendo em qualquer navegador (o domínio, o `www.` e os subdomínios comuns — ver "Subdomínios no hosts"; hosts não tem curinga).
- **App reaberto pelo Watchdog**: com as proteções permanentes ligadas, se o app for fechado
  (ex.: "Sair" na bandeja) o Watchdog o reabre, para o vigia não ficar desligado.
Limites: só vê o navegador em primeiro plano e o que aparece na barra de endereço; não avalia
enquanto você digita (campo com foco); o Chrome/Edge podem ligar o modo de acessibilidade ao serem
consultados (custo pequeno de desempenho). Quem tem poder de Administrador ainda pode encerrar o
app e o serviço (ver "Limite honesto").

**Por que "conteúdo adulto" não bloqueava antes:** o termo só existia na lista de sugestões
(que vale com bloqueio ativo e só se você a cadastrasse); a lista sempre-ativa não o tinha. A
extensão também só bloqueava se o app reportasse as proteções ligadas — agora a lista é
embutida na extensão e funciona mesmo com o app fora do ar. Na aba Status há uma linha
"Extensão no navegador: ATIVA/NUNCA contatou" — é a prova de que ela está carregada, e o botão
"Instalar extensão manualmente" guia o carregamento sem compactação (a alternativa enquanto a
extensão não está publicada nas lojas).

**1e. Atualizar sem desinstalar (o Watchdog.exe fica "em uso").**
Parar o serviço e matar o processo não basta, porque tudo isto reage: o serviço tem recuperação
automática (reinicia em 5 s, `sc failure`), o app reinicia o serviço ao abrir, o Watchdog reabre o
app (proteções permanentes) e a pasta tem a exclusão negada. Use:
- `quick-install.ps1` (já chama o abaixo e **sempre retoma** no fim, mesmo se a cópia falhar);
- `protection-pause.ps1 -Action Pause` → copie/compile/instale o que quiser → `-Action Resume`
  (serve também para compilar no Visual Studio quando o serviço roda da pasta de build);
- o `Installer.iss` já faz a pausa e a retomada em `PrepareToInstall` / `CurStepChanged`.
**A pausa tem portão (`pause-gate.ps1`)**: antes era só rodar o script e digitar ATUALIZAR (ou `-Force`) para
derrubar tudo, até no meio de um bloqueio. Agora, para pausar (script, `quick-install.ps1` e atualização
por cima pelo `Installer.iss`):
- **bloqueio temporizado ou trava de tela ativos = recusado**, sem exceção e sem senha: espere acabar;
- fora do bloqueio, exige a **senha do SharkBlocker** (3 tentativas), conferida contra o hash PBKDF2 do app;
  ATUALIZAR só vale se nunca houve senha (primeira instalação); `state.json` ilegível = recusado;
- o `quick-install.ps1` **não pede a senha** (usa `-SkipPassword`), mas a recusa durante bloqueio/trava continua; só o
  `protection-pause.ps1` chamado isolado e o instalador pedem a senha;
- se o `quick-install.ps1` falhar ou levar Ctrl+C depois da pausa, ele retoma as proteções **e reabre o app** da
  instalação que ficou valendo (`Restart-App`);
- o parâmetro `-Force` foi **removido** (`protection-pause.ps1` e `quick-install.ps1`); `Resume` não pede senha;
- o instalador mostra uma página pedindo a senha ao atualizar por cima (instalação silenciosa:
  `/PausePass=senha`); qualquer falha ao rodar o portão = recusa;
- cada tentativa fica em `ProgramData\SharkBlocker\pause-gate-log.txt`.
Limite honesto: o portão fecha o caminho fácil. Um Administrador determinado ainda pode usar `sc.exe`,
`icacls`, `regedit` ou o modo de segurança (ver ARCHITECTURE.md).
Enquanto pausado, nada está protegido: rode o `Resume` logo em seguida.
Se o serviço roda de OUTRA pasta que a do `-Destination`, o `quick-install.ps1` avisa: copiar para
a pasta errada não atualiza o Watchdog que está valendo.
Correção junto: o `quick-install.ps1` copiava `BrowserExtension` como pasta; quando o destino já
existia isso criava `BrowserExtension\BrowserExtension` e **deixava a extensão antiga no lugar**.
Agora copia o conteúdo.

**2. SafeSearch forçado — agora de verdade.**
Antes só ligava se você achasse o botão das proteções permanentes. Agora o app
oferece ativar na primeira abertura. Cobertura: hosts (≈55 domínios Google por país,
YouTube Modo Restrito, Bing), políticas `ForceGoogleSafeSearch`/`ForceYouTubeRestrict`
(Chrome/Edge/Brave), `ForceBingSafeSearch` (Edge) e reforço por parâmetro de URL na
extensão (Google, Bing, DuckDuckGo, Brave Search, Yahoo).

**3. Extensão de bloqueio instalada automaticamente, com palavras-chave adultas sempre ativas.**
- Termos de conteúdo adulto embutidos (`AdultKeywords.cs`) valem o tempo todo, checados
  na URL e na busca (palavra inteira, sem acento: "denuded" não casa com "nude").
  A lista que VOCÊ cadastra continua sendo varrida no texto da página durante o bloqueio.
- **Aviso importante sobre instalação automática:** o Chrome só aceita instalar à força
  uma extensão que não esteja na Chrome Web Store se o PC estiver em domínio / Azure AD /
  Chrome Enterprise Core. Num PC pessoal, a política por arquivo local `.crx` pode ser
  ignorada em silêncio. Caminho que funciona em qualquer PC: publique a extensão na
  Chrome Web Store (visibilidade "não listada") e/ou no Edge Add-ons e crie
  `extension-config.json` na raiz da instalação:
  `{ "chromeWebStoreId": "<id da loja>", "edgeAddonsId": "<id da loja>" }`.
  A aba Status mostra qual modo está sendo usado em cada navegador.

**Bugs corrigidos nesta rodada**
- `ScreenLockWindow.cs` não compilava (namespace `SharkBlocker.App.Dialogs` colide com
  a classe `App`; `Control.ModifierKeys` inexistente; tipos ambíguos WPF×WinForms) e nunca
  era chamado. Reescrito, com uma janela por monitor.
- `ApplyPermanentChromiumPolicies` gravava `ExtensionInstallBlocklist = ["*"]`, que
  **desativava todas as suas outras extensões**. Removido, e o app limpa o que a versão
  antiga deixou (só se o conteúdo for exatamente o do app antigo).
- Caminhos `HKEY_CURRENT_USER\...` abertos dentro de HKLM (criavam uma chave bogus e
  geravam erros a cada 5s) e linha duplicada do Chrome. Corrigido; a chave bogus é removida.
- A instalação forçada gravava sempre no valor `"1"` da lista, sobrescrevendo extensões
  forçadas por outro programa. Agora reaproveita só a entrada do app.
- Extensão: faltava a permissão `storage`; `setInterval` morria com o service worker;
  falha de conexão contava como "desbloqueado"; "tor"/"pia"/"express" casavam dentro de
  palavras comuns; um segundo bloqueio em menos de 5s era deixado passar; `x.com` casava
  com `box.com`. Corrigidos.
- O servidor local de estado respondia `Access-Control-Allow-Origin: *`; agora só a
  extensão pode ler.
- Em `App.xaml.cs`, fechar o diálogo de senha antes da janela principal existir poderia
  encerrar o app (`ShutdownMode` agora é explícito).

> **Não consegui compilar aqui** (sem .NET SDK no ambiente). Revisei o código linha a
> linha e a lógica JS foi testada, mas rode `dotnet build -c Release` e me mande os erros,
> se houver — eu corrijo.

## Instalação rápida pra testes (sem Inno Setup)

Pra iterar rápido sem compilar um instalador toda vez, use o
`quick-install.ps1` incluído: ele publica os dois projetos, copia tudo pra
uma pasta na estrutura certa (App\ e Watchdog\ lado a lado — o que resolve
de vez o problema de o app não achar o Watchdog.exe) e já abre o app
elevado.

```powershell
.\quick-install.ps1
```

Rodadas seguintes, se só mudou código e já publicou antes:
```powershell
.\quick-install.ps1 -SkipPublish
```

O Inno Setup (`Installer.iss`) continua existindo pra quando você quiser
gerar o instalador de verdade pra distribuir, mas não é mais necessário
durante os testes.

## Causa raiz real do bloqueio intermitente (encontrada via log)

O log mostrou **dois processos `Watchdog.exe` diferentes rodando ao mesmo
tempo**, cada um brigando pelo mesmo arquivo hosts sem coordenação — isso
explica os erros de "Acesso negado" e "arquivo sendo usado por outro
processo" espalhados pelo log, e o comportamento errático (funciona, depois
para, sem padrão claro).

**Por que isso acontecia:** toda vez que o app abre, ele reinstala o serviço
(`sc stop` + `sc delete` + `sc create` + `sc start`) pra garantir que está
usando o binário mais recente. Só que `sc stop` apenas *sinaliza* a parada —
não espera de verdade o processo terminar antes do `sc delete` seguir em
frente. Se o processo antigo demorasse um pouco pra encerrar, virava um
**órfão**: continuava rodando pra sempre, sem nenhum serviço registrado
apontando pra ele, e nada mais conseguia pará-lo via `sc stop`. Depois de
várias rodadas de teste, é esperado acumular vários desses.

**Correção (três camadas):**
1. `ServiceInstaller.Uninstall()` agora espera de verdade a confirmação de
   parada (até 5s) antes do `sc delete`.
2. Antes de qualquer instalação/reinstalação, o app agora mata **todo**
   processo chamado "Watchdog" pelo nome — inclusive os que o Windows já
   nem associa mais a um serviço registrado.
3. O próprio Watchdog, ao iniciar, mata qualquer OUTRA instância de si mesmo
   — importante pro caso dele se autorrecuperar sozinho (sem o app aberto)
   e por acaso um órfão antigo ainda estar de pé.

Além disso, com múltiplos processos mexendo no ACL do hosts ao mesmo tempo,
é esperado que ele tenha acumulado entradas conflitantes — por isso o app
agora roda um `icacls /reset` completo (limpa TODAS as entradas explícitas)
toda vez que reinstala o serviço, antes de reaplicar a proteção normal.

Se você quiser limpar a bagunça acumulada agora, sem esperar reabrir o app,
tem um botão novo na aba Status: **"Encerrar Watchdogs órfãos e resetar ACL
do hosts"**.



**Sites bloqueados não bloqueavam de fato, mas o SafeSearch sim.** Achei a
causa: o App e o Watchdog (serviço) são dois processos separados que liam e
escreviam no arquivo hosts cada um por conta própria, sem se coordenar. Como
o Watchdog reforça o hosts a cada 5 segundos, havia uma condição de corrida
real — se os dois escrevessem quase ao mesmo tempo, um podia sobrescrever o
outro com uma versão desatualizada do arquivo. Isso batia com o sintoma
relatado: SafeSearch é escrito por um fluxo mais isolado, enquanto sites
bloqueados dependiam de escritas simultâneas do App E do Watchdog. Corrigido
com um Mutex nomeado do Windows (`Global\SharkBlocker_HostsFileMutex`),
que garante que só um processo mexa no hosts por vez.
Use o botão **"Testar escrita do hosts agora"** pra confirmar rapidinho que
a escrita está realmente funcionando antes de testar um bloqueio inteiro.

**Hosts agora é protegido o tempo todo.** Antes, a proteção contra edição
manual do hosts só valia durante um bloqueio ativo ou com "Proteções
permanentes" ligado. Agora o Watchdog protege o hosts em TODO ciclo, desde
que esteja rodando — não precisa de nada ligado antes pra isso valer.

**App e serviço agora iniciam sozinhos no boot/login.** O Watchdog (serviço)
já iniciava sozinho no boot (`sc create ... start= auto`) — se isso não
pareceu funcionar antes, o mais provável é o serviço nunca ter sido
instalado de fato (ver os problemas de caminho corrigidos nas rodadas
anteriores). Além disso, agora o app principal (a janela de controle) também
se registra pra abrir sozinho no login, via Agendador de Tarefas com
privilégio máximo — assim não aparece prompt do UAC toda vez que o Windows
liga (uma chave de Registro comum faria isso aparecer). O painel de status
mostra "App abre sozinho no login: sim/não".


## Resposta direta a cada problema relatado

**"É possível alterar a senha durante o bloqueio."**
Bug real, corrigido. `SetPasswordButton_Click` agora verifica `_state.IsLocked`
primeiro e recusa a troca.

**"Não desativa a guia anônima / não trava o SafeSearch / não bloqueia termos adultos / parece não iniciar sozinho / dá pra fechar pelo Task Manager / basta apagar a pasta."**
Isso quase certamente é consequência de **não ter rodado como Administrador**
(você mencionou isso no final da sua mensagem — é a causa mais provável).
Praticamente toda proteção deste app (hosts, Registro, serviço do Windows)
falhava **silenciosamente** sem elevação — parecia que "não fazia nada" mas na
real nunca tinha permissão pra fazer nada. Corrigi isso: o app agora recusa
abrir sem admin, com uma mensagem explicando exatamente o que fazer (botão
direito > Executar como administrador). Rode assim e teste de novo — a
expectativa é que a maioria desses pontos já resolva sozinha.
Além disso, mudei o SafeSearch de "só durante o bloqueio" para uma **proteção
permanente** de verdade (ver seção própria abaixo) e adicionei reforço nativo
no Chrome/Edge (`ForceGoogleSafeSearch`, `ForceYouTubeRestrict`), então agora
tem duas camadas independentes em vez de uma só.

**"Crasha ao importar uma lista grande (porn.txt)."**
Bug real, corrigido. O problema era jogar dezenas de milhares de domínios
dentro de uma caixa de texto do WPF (que não é feita pra isso e trava/estoura).
Agora a importação roda em segundo plano e listas grandes ficam guardadas à
parte (não aparecem mais na caixa de texto) — a interface só mostra um resumo
tipo "porn.txt (58.213 domínios)", com um botão pra remover a lista importada.

**"Dá pra fechar o app e ele para de funcionar / não roda em segundo plano."**
Isso reflete uma confusão de arquitetura que era esperada, não um bug: a
janela do app (SharkBlocker.exe) é só o painel de controle — quem garante
o bloqueio rodando é o **Watchdog**, um Serviço do Windows separado, que deve
continuar rodando mesmo com o app fechado. Se ele não estava rodando, é a
mesma causa raiz da elevação (o serviço só é instalado na primeira vez que o
app roda como admin). Fiz duas coisas: (1) o app agora tem um painel de
"Status do sistema" mostrando se o serviço está instalado e rodando, pra você
conseguir verificar direto; (2) o "X" da janela agora minimiza pra bandeja em
vez de fechar de verdade (só "Sair" pelo menu da bandeja fecha), pra reduzir
a confusão — mas o que garante o bloqueio de verdade continua sendo o
serviço, não a janela.

**"Dá pra finalizar pelo Task Manager."**
O Watchdog (serviço) já tinha recuperação automática configurada — se
alguém finalizar o processo, o Windows deveria reiniciá-lo em segundos. Se
isso não aconteceu no seu teste, o mais provável é, de novo, o serviço nunca
ter sido instalado (sem admin). Com o painel de status novo, dá pra confirmar
isso diretamente. Se o serviço aparecer como "instalado e rodando" e mesmo
assim sobreviver a um "Finalizar Tarefa" sem voltar, aí sim seria um bug pra
investigar — me avise.

**"Basta deletar a pasta (e pede pra instalar o .NET)."**
Duas coisas diferentes aqui, ambas resolvidas:
- O aviso de "instale o .NET" não tinha relação com apagar a pasta — é porque
  o app era publicado no modo "dependente de framework" (precisa do .NET
  Desktop Runtime instalado à parte no Windows). Mudei os projetos pra
  publicarem como **self-contained** (`dotnet publish -r win-x64
  --self-contained true`) — o executável passa a carregar o runtime junto,
  sem depender de nada instalado à parte.
- Apagar a pasta: implementei sua sugestão (ver abaixo).

**"DuckDuckGo ainda acessível pelo Chrome."**
Porque a extensão nunca foi carregada nesse navegador (você mesmo disse que
não testou a pasta `BrowserExtension`) — sem ela, nenhuma proteção de
palavra-chave/DuckDuckGo vale naquele navegador específico. Implementei a
instalação automática (ver seção própria) pra isso não depender mais de você
carregar manualmente.

## Conteúdo adulto em lojas "normais" (ex: livros eróticos via Kindle Unlimited)

A Amazon inteira não dá pra bloquear (é loja normal, bloquear tudo seria
overkill pra maioria das pessoas) — mas as páginas de categoria/gênero desse
tipo de conteúdo têm texto identificável ("Erótica", "Romance Erótico",
etc.), que a extensão de navegador já pega (ela varre tanto a URL quanto o
texto da página, não só a URL). Botão novo: **"Sugerir termos de conteúdo
adulto"**, do lado da caixa de palavras-chave, preenche termos comuns
(erótica, romance erótico, kindle unlimited, hentai, nsfw, etc.) — o mesmo
padrão do botão de navegadores "anti-bloqueio".

**Limite honesto:** isso depende da extensão estar instalada no navegador
usado (ver seção da extensão) e de a página ter texto categorizando o
conteúdo — uma capa de livro isolada, sem nenhum texto de categoria/gênero
por perto, pode escapar da varredura por palavra-chave.

**Se quiser algo mais forte:** dá pra bloquear a leitura do Kindle inteira
(não só a busca/loja) adicionando `read.amazon.com` (Kindle Cloud Reader) na
lista de sites bloqueados — isso impede ler qualquer livro do Kindle pelo
navegador durante o bloqueio, não só conteúdo adulto. Se você tiver o app
oficial do Kindle instalado no Windows, também dá pra adicionar `Kindle` na
lista de programas bloqueados, pra cobrir a leitura fora do navegador.



Implementado em `FolderProtection.cs`, usando o mesmo truque de SID que já
protege o hosts: nega a permissão de **Excluir** (não de ler/executar) pra
Usuários e Administradores na pasta de instalação inteira, recursivamente. O
Watchdog (conta SYSTEM) não é afetado. Faz parte das "proteções permanentes"
abaixo. Limite honesto de sempre: um Administrador que rode `icacls`/`takeown`
na mão ainda reverte — não tem como fechar essa brecha 100% sem tirar do
Windows a capacidade normal de administração.

## Proteções permanentes (novo)

Um novo botão **"Ativar proteções permanentes"** liga, de forma independente
do timer de bloqueio (fica sempre ativo, não só durante um "Iniciar
bloqueio"):
- SafeSearch forçado (hosts + `ForceGoogleSafeSearch`/`ForceYouTubeRestrict`
  nativos do Chrome/Edge + extensão reforçando DuckDuckGo).
- **DNS-over-HTTPS desligado** no Chrome/Edge/Brave (`DnsOverHttpsMode=off`,
  `BuiltInDnsClientEnabled=0`) e no Firefox (`DNSOverHTTPS\Enabled=0` +
  `Locked=1`, o que também impede a pessoa de reativar manualmente nas
  configurações). Isso é o que resolve o pedido de "Firewall que bloqueie
  DoH" — desligar o recurso na raiz via política oficial de cada navegador é
  mais confiável do que tentar bloquear IPs de provedores de DoH por
  Firewall (esses IPs mudam; a política, não).
- Extensão instalada automaticamente no Chrome/Edge/Brave, sem precisar de
  "Modo do desenvolvedor" — ver seção seguinte.
- Pasta protegida contra exclusão.

Ativar não pede senha (é só aumentar proteção). **Desativar pede a senha**
que você configurou, do mesmo jeito que desinstalar.

## A extensão instala automaticamente?

Sim. Usa `ExtensionInstallForcelist`, a política oficial do Chrome pra
instalar extensões sem interação do usuário e **sem que ele consiga
desativar/remover** pela tela `chrome://extensions` (documentado em
developer.chrome.com/docs/extensions/develop/concepts/extensions-update-lifecycle).
É o que fecha de vez a brecha do "DuckDuckGo acessível pelo Chrome" — uma vez
ativado, a extensão passa a estar sempre lá, em todo perfil do Chrome/Edge/
Brave da máquina, sem o usuário poder tirá-la.

**Não sobrou passo manual nenhum.** O `.crx` assinado (formato CRX3,
documentado publicamente pelo Chromium) é gerado sozinho em C#, usando a
chave em `ExtensionSigningKey/extension_key.pem` — sem precisar do Chrome
instalado pra empacotar. Tudo acontece automaticamente ao clicar em "Ativar
proteções permanentes".

**Validação:** o formato exato que uso (`extension_id;file:///caminho/
manifesto.xml` na política) é o mesmo documentado pela UiPath pra distribuir
a extensão Chrome deles via instalação offline por Política de Grupo
(docs.uipath.com/studio/standalone/latest/user-guide/chrome-group-policies)
— um caso real, em produção. Isso não substitui testar no seu navegador,
mas é uma confirmação forte de que o esquema funciona sem precisar de
políticas extras.

**Se não aparecer:** o Chrome só relê políticas nova em algumas situações
(abrir o navegador, ou periodicamente). Se ele já estava aberto quando você
ativou, feche e abra de novo. Pra conferir o que o Chrome está enxergando de
verdade, acesse `chrome://policy` — se `ExtensionInstallForcelist` aparecer
lá com nosso ID, a política chegou; se a extensão ainda não aparecer em
`chrome://extensions` depois disso, o problema é o Chrome não conseguir
baixar/validar o `.crx` (confira o log de diagnóstico do app).

**Coisas que podem bloquear isso, mesmo com tudo certo do nosso lado**
(raro numa máquina pessoal, mais comum em PC de empresa/gerenciado):
`ExtensionInstallBlocklist` com `*`, `ExtensionAllowedTypes` sem
`"extension"`, ou `BlockExternalExtensions` ligado. Se algo assim já estiver
configurado na sua máquina por outro motivo, nosso `ExtensionInstallForcelist`
pode não bastar sozinho.

Cobre Chrome/Edge/Brave. Não cobre Firefox (sistema de extensões diferente).

## AppLocker automatizado (novo)

Botão **"Aplicar regras AppLocker"** gera e aplica, via PowerShell
(`Set-AppLockerPolicy`), regras que impedem os navegadores "anti-bloqueio"
conhecidos de sequer **abrir** (o Windows recusa a execução, é mais forte que
matar o processo depois de aberto).
- **Só existe no Windows 11 Pro/Enterprise/Education** — o app detecta a
  edição e avisa se não for suportado (não tem contorno pra isso, é
  limitação do Windows Home mesmo).
- São regras por CAMINHO de instalação padrão — contornável movendo o
  executável de pasta. Regras por hash/editor seriam mais fortes, mas não dá
  pra gerar isso de forma genérica sem saber de antemão o instalador exato de
  cada programa.
- Não incluí um botão de "remover" no app: `Set-AppLockerPolicy` com uma
  política vazia reseta a política de AppLocker da máquina INTEIRA, não só as
  regras deste app — se sua organização/PC já tiver outras regras de
  AppLocker, isso as apagaria também. Se precisar reverter, faça manualmente
  pelo Editor de Política de Grupo Local (`gpedit.msc` >
  Configurações do Computador > Configurações do Windows > Configurações de
  Segurança > Políticas de Controle de Aplicativos > AppLocker) ou avise que
  posso te passar o comando exato pro seu caso.

## Como compilar e publicar
```
dotnet build -c Release
dotnet publish App -c Release -r win-x64 --self-contained true
dotnet publish Watchdog -c Release -r win-x64 --self-contained true
```
Saem em `App\bin\Release\net8.0-windows\win-x64\publish\` e o equivalente
pro Watchdog — são esses os caminhos que o `Installer.iss` já espera.

## Como gerar o instalador (Setup.exe)
Mesmo processo de antes — `Installer.iss` com Inno Setup
(https://jrsoftware.org/isdl.php), agora apontando pros caminhos de publish
self-contained. O desinstalador gerado continua chamando
`SharkBlocker.exe --uninstall` antes de apagar qualquer coisa, então a
senha vale tanto pelo app quanto pelo "Adicionar ou Remover Programas".

## Como instalar
1. Publique (comandos acima) ou gere o Setup.
2. Rode como **Administrador** — o app recusa abrir sem isso agora, com
   instruções na tela.
3. Defina uma senha.
4. (Opcional) Gere o `extension.crx` (ver seção da extensão) e clique em
   "Ativar proteções permanentes".
5. (Opcional, Windows Pro/Enterprise) Clique em "Aplicar regras AppLocker".

## Tabela-resumo de proteções e limites reais

| Proteção | O que impede | Limite honesto |
|---|---|---|
| Elevação obrigatória | Falhas silenciosas sem admin | N/A — é um requisito, não uma trava |
| Sem botão de desbloqueio + HMAC | Editar JSON/Registro pra encurtar o tempo | Exige engenharia reversa do binário |
| Senha bloqueada durante o lock | Trocar/resetar senha no impulso | Fora do lock, ainda dá pra trocar sabendo a senha atual — é intencional |
| Serviço com auto-restart | Matar processo no Task Manager | `sc stop` administrativo limpo ainda funciona — por isso a senha no botão de proteções/desinstalar |
| Pasta protegida (ACL) | Apagar pelo Explorer, mesmo como admin | `icacls`/`takeown` manual ainda reverte |
| Hosts com ACL + watcher + Mutex entre processos, SEMPRE ativo | Edição manual, inclusive Notepad elevado; corrida de escrita App×Watchdog | `icacls`/`takeown` manual ainda reverte |
| SafeSearch permanente (hosts + prefs nativas + extensão) | Desligar SafeSearch no Google/Bing/YouTube; reforça DuckDuckGo | Cobre só navegador/PC |
| DoH desligado via política | Navegador ignorar o hosts via DNS-over-HTTPS | Só cobre Chrome/Edge/Brave/Firefox; outro navegador pode não respeitar a política |
| Extensão forçada (não removível) | Desativar/remover a extensão pela tela do navegador | Só Chrome/Edge/Brave; exige o passo manual do `.crx` |
| Filtro de proxy/VPN/navegador remoto (extensão + vigia de desktop + hosts) | Entrar em proxies e navegadores na nuvem; usar proxy como atalho pra site bloqueado; remover a extensão | Proxy novo com nome totalmente opaco passa até entrar na lista; o vigia só vê o navegador em primeiro plano; não vê o conteúdo dentro de um navegador remoto |
| AppLocker | Abrir navegadores "anti-bloqueio" instalados em caminho padrão | Só Windows Pro/Enterprise; contornável movendo o executável |
| Watchdog + WMI mata processos na hora | Uso de apps/navegadores fora dos caminhos do AppLocker | — |
| Extensões de VPN/proxy (política `ExtensionSettings`: `"*".blocked_permissions=["proxy"]`) | Instalar/manter extensão que peça a permissão `proxy` no Chrome/Edge/Brave, sem lista de IDs | Bloqueia também extensões legítimas que usem `proxy`; só Chromium; modo desenvolvedor pode escapar; **validar em `chrome://policy`** |
| VPNs conhecidas (WARP, Psiphon, Lantern, TunnelBear, UltraSurf + as anteriores) por nome/caminho/AppLocker; VPN nativa do Windows desconectada (`rasdial`) no bloqueio | Usar VPN instalada ou a VPN do Windows durante o bloqueio | Nome totalmente opaco passa; AppLocker só Pro/Enterprise; reconexão é desfeita a cada ~3-5 s, não impedida |
| Serviço registrado para o **modo seguro** (chaves `SafeBoot\Minimal` e `\Network`) | Reiniciar em Modo Seguro (com ou sem rede) para editar o hosts sem o Watchdog | Acesso físico, BIOS destravada, outro sistema de boot, ambiente de recuperação ou edição offline do Registro passam; no modo seguro o WMI não é usado (encerramento de apps só no ciclo de 5 s) |

Como sempre: nada disso, junto ou separado, impede 100% um Administrador
local realmente decidido. O objetivo é atrito real o suficiente pra conter o
impulso do momento.

## Modo seguro (auditoria U1)

Por padrão o Windows **não carrega serviços de terceiros** no modo seguro; sem tratamento, o Watchdog não subiria
e ninguém reverteria edições no `hosts`. Agora:

- `ServiceInstaller.Install` grava o valor padrão `Service` em
  `HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\SharkBlockerWatchdog` e em
  `...\SafeBoot\Network\SharkBlockerWatchdog` (o mesmo mecanismo que antivírus usam). `install-service.ps1` faz o mesmo
  e confere; `health-check.ps1` só avisa se faltarem.
- `ServiceInstaller.EnsureSafeBootRegistration()` é **idempotente** (só grava se faltar ou diferir) e é chamado pelo
  Watchdog a cada ~60 s, junto de `EnsureFailureActionsConfigured`: apagar as chaves não desliga a proteção em silêncio
  (elas voltam e o fato vai para o `app.log`). Só registra se o serviço existe e não está marcado para exclusão.
- **Desinstalar** (`ServiceInstaller.Uninstall`, depois da senha) remove as duas chaves. **Pausar** (`protection-pause.ps1`,
  instalador) NÃO as apaga: só desabilita e para o serviço, que continua registrado.
- Ao subir, o Watchdog lê `GetSystemMetrics(SM_CLEANBOOT)`; se for 1 (seguro) ou 2 (seguro com rede), registra no log e
  aplica hosts, SafeSearch, políticas e encerramento de apps **na hora** (`EnforceImmediately`), antes da inicialização lenta.
- Componentes que dependem do que falta no modo seguro falham com tolerância (log, sem derrubar o serviço): o servidor
  local (`http.sys`), o `HostsWatcher`, o pipe de status e o `ProcessStartWatcher`, que **nem inicia o WMI** no modo seguro.

**Limites honestos.** Quem tem acesso físico, BIOS/UEFI destravada, outro sistema de boot, ambiente de recuperação (WinRE)
ou edita o Registro offline ainda passa. Quem apaga as chaves e reinicia em modo seguro em menos de ~60 s também. No modo
seguro a extensão do navegador, o app e a trava de tela podem não estar disponíveis: só valem o `hosts` e as políticas
em HKLM aplicados pelo Watchdog. Veja os testes manuais abaixo.

**Teste manual (exige Windows real):** reiniciar em Modo Seguro com Rede; `sc query SharkBlockerWatchdog` deve mostrar
RUNNING; editar o `hosts` e ver a edição revertida; durante um bloqueio, abrir um navegador da lista e ver o processo
encerrado em até ~5 s; no `app.log` deve constar "MODO SEGURO COM REDE detectado".

## Trava de tela no bloqueio agendado + Ellen brava inteira

- **Horário de bloqueio agendado = tela travada até o fim do bloqueio.** Dentro de uma janela da aba *Agenda* (e logo ao ligar o PC,
  se ainda estiver dentro dela), o app mostra a trava de tela em todos os monitores até o fim da janela (ou da cadeia de janelas coladas).
  `ScreenLock.ShowScheduledBlockIfDue` é chamado ao abrir o app (`App.OnStartup`) e a cada tique do app (`MainWindow.Timer_Tick`), então a trava
  volta sozinha se cair. O fim é **recalculado da agenda** (sem gravar nada novo): reiniciar o PC ou reabrir o app não zera a espera.
  A contagem passa a mostrar **H:MM:SS** quando falta 1 h ou mais. O teto de segurança dessa trava é de 72 h (as outras continuam em 20 min).
  Bloqueio iniciado **à mão**, fora de uma janela, continua sem trava de tela (só bloqueia sites/apps). A aba Agenda avisa disso.
  LIMITES HONESTOS: os mesmos da trava normal — Ctrl+Alt+Del não é bloqueável e o botão de desligar sempre funciona (válvula de
  segurança); o Gerenciador de Tarefas fica desativado enquanto a trava durar (horas, neste caso) e é restaurado ao fim ou na próxima abertura do app.
  Durante a trava não dá para pausar/atualizar/desinstalar (o portão recusa durante bloqueio/trava).
- **Imagem da Ellen brava (`Assets/ellen-angry.png`).** A arte antiga estava recortada (topo e pernas cortados). Agora é de corpo inteiro (761×1763,
  com margem transparente), e o layout da trava reserva o espaço do palco da imagem dos dois lados do painel central; o painel de texto fica
  num `Viewbox` que só encolhe, então em tela baixa/estreita o texto diminui em vez de ser cortado ou de passar por trás da imagem.

## VPN e proxy (auditoria U2)

- **Extensões de VPN/proxy da Web Store, sem depender de IDs.** `BrowserPolicyManager` grava a política `ExtensionSettings`
  (REG_SZ com JSON) em Chrome, Edge e Brave com `"*": { "blocked_permissions": ["proxy"] }`: o navegador não instala (e desativa) extensão
  que peça a permissão `proxy`, que é o que toda VPN/proxy de extensão precisa. **Mescla** com o `ExtensionSettings` que já existir
  (preserva o resto do JSON); se o valor existente não for um JSON de objeto legível, **não mexe** e só loga. Só roda com as proteções
  permanentes ligadas, idempotente (não regrava se já está certo). Ao desativar as proteções permanentes ou desinstalar, tira **somente o
  `proxy` que o app acrescentou** (marcador em `HKLM\SOFTWARE\SharkBlocker\PolicyMarkers`); se o administrador já tinha `proxy` lá, ele fica.
  **Precisa de validação:** `chrome://policy`, `edge://policy` e `brave://policy` devem mostrar `ExtensionSettings` com status OK.
  Limites: bloqueia também extensão legítima que use `proxy`; só Chromium; extensão carregada em modo desenvolvedor pode escapar.
  Não há lista de IDs (nada inventado). A alternativa com `chrome.management` na extensão **não foi implementada** (exigiria a permissão
  `management` e novo `.crx`).
- **Programas.** `ProcessBlocker` (nomes e trechos de caminho específicos) e `AppLockerManager` ganharam Cloudflare WARP (`warp-svc`,
  `warp-taskbar`, `Cloudflare WARP`), Psiphon, Lantern, TunnelBear e UltraSurf. Sem trechos genéricos de caminho; `NeverKill` intacto.
- **Opção desligada por padrão** (aba *Proteções* → "VPNs de uso comum"): inclui Tailscale, ZeroTier, Hamachi, ngrok e cloudflared
  (`BlockState.BlockOptionalVpnTools`). Ligar não pede senha; desligar passa por `UninstallFlow.TryAuthorizeRemoval`.
- **VPN nativa do Windows.** `NativeVpnGuard` (compartilhado com o Watchdog), durante o bloqueio temporizado, detecta adaptadores PPP/RAS em
  estado "Up" e roda `rasdial "NOME" /disconnect` (com `rasdial /disconnect` geral como reserva), registrando no log. Adaptadores de túnel
  de terceiros (TAP/Wintun/WireGuard/WARP...) só são **registrados**. O app (sessão do usuário) cobre as conexões "só deste usuário"; o
  Watchdog (SYSTEM) as "para todos os usuários". LIMITE HONESTO: a pessoa pode reconectar; a próxima varredura (~3-5 s) desconecta de novo.

**Testes manuais (exigem Windows):** (1) criar uma extensão com `"permissions": ["proxy"]` (modo desenvolvedor e/ou empacotada) e conferir
que é bloqueada/desativada; conferir `chrome://policy`; criar antes um `ExtensionSettings` de teste com outra entrada e ver que ela continua
lá depois de ativar e depois de desativar as proteções permanentes; (2) com bloqueio ativo, conectar uma VPN nativa (Configurações > Rede >
VPN) e ver a desconexão e a linha no `app.log`; (3) abrir `warp-svc`/Psiphon/Lantern/TunnelBear (se tiver) e ver o encerramento; (4) ligar a
opção "VPNs de uso comum" e repetir com Tailscale; desligar e ver o pedido de senha.

## Subdomínios no hosts (auditoria U3)

O arquivo `hosts` **não aceita curinga**: bloqueia por nome exato. Antes só entravam `site.com` e `www.site.com`, então `m.site.com`,
`pt.site.com` etc. resolviam normalmente onde a extensão não roda (Firefox, apps com navegador embutido, anônimo fora do bloqueio).

- **O que muda.** Para cada site que a pessoa digitou, `HostsFileManager` agora também grava `m.`, `mobile.`, `amp.`, `touch.`, `en.`, `pt.`,
  `pt-br.`, `es.`, `fr.`, `de.` e `it.` (`HostsFileManager.DefaultSubdomainPrefixes`). A geração é feita **dentro do `HostsFileManager`**, e
  todos os chamadores (app, `HostsWatcher`, Watchdog) passam `BlockState.HostsSubdomainPrefixes`, então todos montam exatamente o mesmo bloco
  (senão um reescreveria o do outro a cada 5 s).
- **Configurável.** `BlockState.HostsSubdomainPrefixes` (no `state.json`): `null` = lista padrão; lista própria = só esses prefixos (um rótulo DNS
  por item, minúsculo, sem `www`, no máximo 40); lista vazia `[]` = sem expansão de subdomínios.
- **Tamanho e desempenho.** Só os **500 primeiros** sites da lista são expandidos (`MaxExpandedDomains`) e só se forem domínio-raiz
  (`site.com`, `bbc.co.uk`; `old.reddit.com` não é multiplicado). Os sites digitados vêm antes dos importados, então uma lista importada de
  dezenas de milhares de domínios **não** é multiplicada: o `hosts` fica com ≈ o mesmo tamanho de antes (70 mil importados ≈ 145 mil linhas) e
  cada site digitado custa 13 linhas em vez de 2 (no máximo ≈ 6.500 linhas extras). Aparece uma linha no `app.log` se o limite for atingido.
- **LIMITE HONESTO.** Subdomínios fora da lista (`login.`, `api.`, `cdn.`, `old.`...) continuam livres **no hosts**; onde a extensão roda ela
  bloqueia qualquer subdomínio. Acesso por IP literal também ignora o `hosts`. A alternativa por sufixo do cliente DNS do Windows (NRPT,
  `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DnsPolicyConfig`) cobriria todos os subdomínios, mas **não foi implementada**:
  precisa de teste do comportamento com servidor inexistente (tempo de espera) antes de adotar.

**Teste manual (exige Windows):** bloquear um domínio de teste (ex.: `exemplo.com`) e, com o bloqueio ativo, abrir `m.exemplo.com` e
`pt.exemplo.com` no **Firefox** e em um **app Electron** (ex.: Discord/VS Code com um link) — devem falhar; conferir no `hosts` o bloco entre
`SharkBlocker START/END`. Depois definir `"HostsSubdomainPrefixes": []` no `state.json` (com o app e o Watchdog lendo o estado) e ver as linhas
de subdomínio sumirem, sem o Watchdog e o app brigarem pelo arquivo.

## Usuário ou perfil novo (auditoria U4)

O que já valia para qualquer conta: `hosts`, políticas dos navegadores (HKLM), extensão à força e o Watchdog (SYSTEM), que enxerga processos de
todas as sessões. O que era preso ao usuário que instalou — e mudou:

- **Tarefa de logon para qualquer conta.** `AppStartupManager` agora registra a tarefa `SharkBlockerApp` com o **grupo Users**
  (`S-1-5-32-545`) como principal e gatilho de logon **sem `UserId`** (antes: só `WindowsIdentity.GetCurrent().Name`). A política de
  instâncias passou a `Parallel` (uma por sessão; com `IgnoreNew` o app da 1ª conta, que fica aberto, impediria o da 2ª). Instalações antigas
  são corrigidas sozinhas: o Watchdog compara o XML da tarefa a cada ~60 s e a recria no formato novo (no máximo 3 tentativas por execução).
- **Um app por sessão ativa.** O Watchdog enumera as sessões (`WTSEnumerateSessions`, só as ativas, sem a 0) e abre o app em **cada** uma que
  não tenha um `SharkBlocker.exe` (checa `Process.SessionId`), com o token daquela conta (`UserSessionLauncher.LaunchInSession`). Antes bastava
  qualquer app vivo e só a sessão do console era atendida: com troca rápida de usuário a outra ficava sem trava de tela e sem o vigia da
  barra de endereço. Intervalo: ~5 s por sessão (um ciclo; no mínimo 4 s entre tentativas), em vez de 20 s.
- **Falhas não viram spam.** Tela de login (erro 1008): tenta de novo em 5 s, sem ruído. Conta padrão (erro 740): espera 5 min. Outro erro: espera
  crescente (30 s × falhas, até 5 min) e tenta o Agendador como plano B.
- **Parar o serviço dá "acesso negado".** `ServiceInstaller.EnsureStopProtection` grava no serviço um descritor de segurança com uma **negação de
  Parar/Pausar (`WP`,`DT`) para Administradores** (`sc stop`, `services.msc`, `Stop-Service`). Lê o descritor do Registro e só chama `sc sdset`
  se a negação sumiu; roda na instalação e a cada ~60 s no Watchdog. Iniciar, consultar, reconfigurar, apagar e regravar o descritor continuam
  permitidos. **Pausa, atualização e desinstalação** (já atrás de senha) voltam ao descritor padrão antes de parar o serviço
  (`ServiceInstaller.ClearStopProtection`, `protection-pause.ps1`, `Installer.iss`, `install-service.ps1`, `emergency-fix-hosts.bat`).

**LIMITES HONESTOS**
- **Conta padrão (sem administrador): o app não abre.** Ele exige administrador (`app.manifest`); o Windows recusa (erro 740) e a trava de tela e o
  vigia da barra de endereço **não existem nessa sessão**. `hosts`, políticas, bloqueio de processos e VPNs continuam valendo (são do Watchdog).
  Abrir o app como SYSTEM dentro da sessão do usuário foi **descartado de propósito**: janela de diálogo de arquivo rodando como SYSTEM = escalada de
  privilégio. Quem cria a conta nova é administrador; se criar uma conta padrão, essa camada de desktop fica de fora.
- **A negação de Parar é atrito, não barreira.** Um administrador pode regravar o descritor (`sc sdset`) e parar o serviço; a tarefa de reavivamento
  (1 min) e a detecção de encerramento forçado continuam sendo a rede de segurança. A tarefa de reavivamento também pode ser desativada por um admin.
- O token do SYSTEM também contém o grupo Administradores, então a negação alcança o SYSTEM para "Parar" por SCM. Nada do app depende disso.

**Testes manuais (exigem Windows):** (1) criar uma conta **administradora** nova e entrar (com a 1ª conta ainda logada, via troca de usuário):
o app deve aparecer na 2ª sessão em ~5-15 s (veja `app.log`: "Sessão N sem o app — abrindo"); (2) o mesmo com uma conta **padrão**: deve aparecer
uma única linha de erro 740 e `hosts`/bloqueio seguir funcionando; (3) `schtasks /Query /TN SharkBlockerApp /XML` deve conter `S-1-5-32-545` (numa
instalação antiga, até ~1 min depois do Watchdog subir); (4) `sc stop SharkBlockerWatchdog` num prompt elevado deve dar **acesso negado**, e o
`sc sdshow SharkBlockerWatchdog` deve começar com `D:(D;;WPDT;;;BA)`; (5) pausar as proteções (com senha) e desinstalar/atualizar devem continuar
funcionando; (6) `emergency-fix-hosts.bat` deve parar o serviço.

## Políticas de navegador: visitante, DoH e anônimo (auditoria U5)

- **Sem modo visitante.** `BrowserPolicyManager.ApplyPermanentChromiumPolicies` grava `BrowserGuestModeEnabled = 0` em Chrome, Edge e Brave
  (camada permanente; sai junto com as proteções permanentes). O perfil visitante não carrega extensões, então era uma janela sem a nossa.
- **DNS-sobre-HTTPS desligado também no Opera e no Vivaldi.** `DnsOverHttpsMode = "off"` e `BuiltInDnsClientEnabled = 0` em
  `HKLM\SOFTWARE\Policies\Opera Software\Opera Stable` e `HKLM\SOFTWARE\Policies\Vivaldi` (antes só Chrome, Edge, Brave e Firefox). As chaves
  continuam existindo ao remover (podem ter outras políticas); só os dois valores são apagados.
- **Anônimo sempre bloqueado (opção, DESLIGADA por padrão).** Novo botão na aba de proteções e `BlockState.BlockIncognitoAlways`. Com a opção ligada
  **e** as proteções permanentes ativadas, `IncognitoModeAvailability`/`InPrivateModeAvailability`/`DisablePrivateBrowsing` ficam gravados o tempo todo,
  não só durante o bloqueio temporizado (fecha a brecha de a extensão não rodar no anônimo fora do bloqueio). Ligar não pede senha; desligar passa
  por `UninstallFlow.TryAuthorizeRemoval` (trava de 30 min + senha + confirmações). Desativar as proteções permanentes também desfaz esse bloqueio
  (se não houver bloqueio temporizado em andamento). Comportamento padrão = o de antes.
- **Bônus (robustez).** `ApplyRestrictions` agora é idempotente (só grava se mudou) e o Watchdog a chama a cada ciclo durante o bloqueio; antes valia
  "uma vez por bloqueio" e apagar o valor no meio dele não era corrigido.

**LIMITES HONESTOS**
- Opera e Vivaldi **não** recebem a política de visitante nem SafeSearch/`ExtensionSettings` (só o DoH); a extensão à força continua só no Chrome, Edge e Brave.
- Se o Watchdog for reiniciado depois de a opção ser desligada e o valor já estiver gravado, ele não sabe que o valor é nosso (a memória é por
  execução): nesse caso o valor sai no próximo fim de bloqueio ou ao desativar as proteções permanentes.
- A política vale para quem reler o Registro: abra o navegador de novo depois de ligar a opção; as já abertas só atualizam ao reler as políticas.

**Teste manual (exige Windows):** com as proteções permanentes ativas — (1) `chrome://policy`, `edge://policy`, `brave://policy`: `BrowserGuestModeEnabled`
= false com status OK; tentar abrir o perfil visitante deve falhar; (2) `opera://policy` e `vivaldi://policy`: `DnsOverHttpsMode` = off e
`BuiltInDnsClientEnabled` = false (se o navegador não listar a política, a validação falhou — me avise); (3) ligar "Anônimo sempre bloqueado": fora de
qualquer bloqueio, Ctrl+Shift+N (Chrome/Brave/Opera/Vivaldi), Ctrl+Shift+P (Edge/Firefox) não devem abrir janela privada; (4) desligar a opção (com senha)
e conferir que o anônimo volta; (5) com a opção ligada, iniciar e terminar um bloqueio temporizado: o anônimo deve continuar bloqueado depois dele.

## Navegadores renomeados, portáteis e derivados (auditoria U6)

Antes o `ProcessBlocker` comparava só o **nome do processo**. Renomear `firefox.exe` para `calc2.exe`, copiar um navegador portátil ou usar um
derivado de Chromium fora da lista (Yandex, Iron, Arc...) escapava. Agora, durante o bloqueio temporizado, ele também lê os **metadados do arquivo**
(`FileVersionInfo`: `ProductName`, `FileDescription`, `InternalName`, `OriginalFilename`, `CompanyName`), que sobrevivem ao renomear.

- **Sempre encerrados (em qualquer pasta):** navegadores que a nossa extensão não cobre — Firefox e derivados (inclui Tor Browser, LibreWolf, Waterfox,
  Floorp, Mullvad Browser, Pale Moon), Opera, Vivaldi, Yandex, SRWare Iron, Arc, Epic, DuckDuckGo, Comodo Dragon, Slimjet, Cent, Maxthon, Thorium,
  Supermium, Falkon, Avast/AVG Secure Browser e **Chromium** (que só estava no vigia de barra, não na lista de processos).
- **Encerrados só FORA dos diretórios de instalação padrão:** Chrome, Edge e Brave. Instalados em `Program Files`/`Program Files (x86)` ou nas pastas por
  usuário do instalador (`AppData\Local\Google\Chrome*`, `BraveSoftware\Brave-Browser*`, `Microsoft\Edge*`, sempre `...\Application\`) ficam livres; cópia
  portátil ou renomeada fora delas, não. Um `chrome.exe` de origem desconhecida (metadado `OriginalFilename = chrome.exe` sem assinatura conhecida) segue a
  mesma regra.
- **Nomes exatos novos** na lista: `chromium`, `waterfox`, `librewolf`, `floorp`, `palemoon`, `thorium`, `supermium`, `maxthon`, `slimjet`, `falkon`.
- **Mantido:** `NeverKill`, a proteção do próprio app/Watchdog, as listas e trechos de caminho da pessoa e todas as exceções anteriores. Pastas do Windows
  nunca são examinadas. Falha ao ler o arquivo = não mata. O log agora diz o **motivo** de cada encerramento (nome, trecho de caminho ou metadados).
- **Comparação por palavras inteiras**, nunca por trecho: um jogo chamado "Brave Frontier" só casa com a empresa certa; WebView2 (inclusive o embutido em
  programas portáteis) e atualizadores do Edge são excluídos. Resultado em cache por arquivo (invalida quando o arquivo muda), então a varredura não relê
  o disco a cada ciclo.

**LIMITES HONESTOS**
- Os textos de metadados das assinaturas foram escritos de memória (é como cada navegador costuma se identificar), **não testados** aqui. Para conferir um
  navegador real: `(Get-Item 'C:\caminho\navegador.exe').VersionInfo | Format-List *`; se não casar, ajuste ou acrescente uma assinatura em
  `ProcessBlocker.BrowserSignatures`.
- Programa portátil que **não** tem metadados (ou os apagou/alterou) continua escapando; nesse caso só valem o nome e os trechos de caminho.
- Só vale durante o bloqueio temporizado (como o resto do `ProcessBlocker`). Fora dele nada é encerrado.
- Um programa legítimo cujo metadado `OriginalFilename` seja `chrome.exe` e que rode fora de `Program Files` será encerrado durante o bloqueio; se isso
  acontecer, o log mostra o motivo e o caminho — acrescente a pasta aos diretórios padrão ou ajuste a regra.

**Teste manual (exige Windows):** (1) copie `firefox.exe` (ou a pasta de um Firefox portátil) para a Área de Trabalho, renomeie para `calc2.exe`, inicie
um bloqueio e abra: deve ser encerrado e o `app.log` deve trazer "motivo: navegador identificado pelos metadados do arquivo: Firefox"; (2) copie a
pasta de um Chrome para `D:\Portatil\` e renomeie o `chrome.exe`: encerrado; (3) o Chrome/Edge/Brave **instalados** devem continuar abrindo durante o
bloqueio; (4) apps com WebView2 (Teams, Widgets do Windows) e Electron (Discord, VS Code) não podem ser afetados; (5) Yandex/Iron/Chromium, se você
tiver algum, devem ser encerrados mesmo instalados.

## Tradutor, cache, arquivo web, IP literal e DoH por nome (auditoria U7)

Fecha quatro desvios pequenos que ainda passavam pela extensão e pelo `hosts`.

- **Tradutor de páginas e cache de buscador** (`proxy-terms.js`, lista `gatewayServices`): `translate.google.*/translate|website|translate_c|translate_p`,
  `translate.goog`, `translate.googleusercontent.com`, `translatetheweb.com`, `microsofttranslator.com`, `translate.yandex.*/translate`, `webcache.googleusercontent.com`,
  `cc.bingj.com` e outros caches. O tradutor de TEXTO (`translate.google.com/`) continua abrindo. A extensão já avaliava o destino de `?u=`, `translate.goog` e
  `cache:https://...`; agora o próprio serviço também é barrado (pega destino sem esquema, formatos novos). Se o destino já cai numa regra mais específica
  (site da sua lista, adulto), vale o motivo do destino.
- **Arquivo web** (lista `archiveServices`: Wayback Machine, archive.today e similares, só `archive.org/web` e não o archive.org inteiro): uso legítimo grande, então
  é uma **opção DESLIGADA por padrão**, botão "Arquivos da web (opcional)" na aba Proteções. Ligar não pede senha; desligar pede senha e confirmações. A
  extensão lê a opção em `/state` (`blockWebArchives`), no máximo ~30 s depois.
- **IP literal** (`matcher.js`): durante o bloqueio, navegar para um IP público (IPv4 ou IPv6, inclusive `::ffff:a.b.c.d` e NAT64) é barrado, porque o `hosts`
  não vê IP. Formas como `http://2130706433/` ou `0x7f.1` o próprio navegador converte antes. Passam: `localhost`, `127.0.0.0/8`, `10/8`, `172.16/12`, `192.168/16`,
  `169.254/16`, `100.64/10`, `::1`, `fe80::/10`, `fc00::/7`.
- **DoH por nome** (`dohHosts`, ~27 nomes: `dns.google`, `cloudflare-dns.com`, `mozilla.cloudflare-dns.com`, `dns.quad9.net`, `dns.nextdns.io`, `dns.adguard.com`...):
  entram no `hosts` apontando para `127.0.0.1` (camada permanente), então o navegador não acha o resolvedor. É a 2ª camada: a 1ª continua sendo a política
  que desliga o DoH.

Os três primeiros mostram só a página de bloqueio (motivos `gateway` e `ip`), **sem** a trava de tela de 30 min, como o motivo `url`; só valem durante o bloqueio.

**LIMITES HONESTOS**
- O vigia de barra de endereço do desktop só age nos motivos que travam a tela; então tradutor, cache, arquivo web e IP literal só são barrados em navegador
  **com a extensão**. Também não foram para o `hosts` durante o bloqueio (as quatro rotas que montam a lista do hosts não foram mexidas).
- DoH no `hosts`: só nomes exatos (sem curinga; `<id>.dns.nextdns.io` não é coberto); a lista é de memória e não foi conferida contra os provedores. Um
  navegador com DoH em modo "seguro" (sem fallback) simplesmente deixa de resolver nomes em vez de cair no DNS do sistema. Quem usa DoH do Windows 11
  por IP não é afetado. Se algum programa seu depender de um desses nomes, tire-o da lista `dohHosts`.
- Lista de tradutores/caches/arquivos escrita de memória: serviços mudam ou somem (o cache do Google, por exemplo, foi desativado). Acrescente o que escapar.
- IP em outras portas ou por redirecionamento de um site comum só é visto na navegação de nível superior, como o resto da extensão.

**Teste manual (exige Windows + Chrome com a extensão):** (1) iniciar um bloqueio e abrir `http://8.8.8.8/` → página de bloqueio "Acesso por IP"; `http://192.168.0.1/`
e `http://127.0.0.1:57411/state` continuam abrindo; (2) `https://translate.google.com/translate?sl=auto&tl=pt&u=exemplo.org` → bloqueado; `https://translate.google.com/`
abre; (3) ligar "Arquivos da web", esperar ~30 s e abrir `https://web.archive.org/` → bloqueado; desligar (senha) e conferir que volta; (4) com as proteções permanentes,
`type %SystemRoot%\System32\drivers\etc\hosts` deve listar `127.0.0.1 dns.google` etc. dentro do bloco de SafeSearch; (5) `nslookup dns.google` deve falhar/apontar
para 127.0.0.1; (6) fora do bloqueio, nada disso é barrado (exceto o DoH no `hosts`, que é permanente).

## Fora do alcance do app: WSL, máquina virtual, live USB e outros aparelhos (auditoria U8)

Nada disso é código: o app **não** consegue resolver sozinho. WSL2 e máquinas virtuais têm rede e `hosts` próprios (o do Windows não vale lá dentro); live USB, outro
celular/tablet/TV e outro computador nem passam pelo Windows. Por isso o que existe aqui é uma lista de mitigações que você aplica fora do SharkBlocker.

| Brecha | O que fazer (fora do app) |
|---|---|
| Outros aparelhos e a rede inteira | **DNS de filtragem no roteador**: OpenDNS FamilyShield, CleanBrowsing (filtro adulto) ou NextDNS. Troque a senha de administrador do roteador e guarde-a com outra pessoa. |
| Live USB / boot por outro sistema | **Senha de BIOS/UEFI**, boot por USB/CD desligado e ordem de boot travada. |
| Ler ou editar o disco por fora do Windows | **BitLocker** (com a chave de recuperação guardada fora do PC e com outra pessoa). |
| WSL2 e máquinas virtuais | **Desativar WSL e Hyper-V / Plataforma de Máquina Virtual** se não usar (Recursos do Windows). Se usar, o filtro no roteador é a proteção. |
| Modo seguro e acesso físico | Tratados em parte na U1; quem tem BIOS destravada e acesso físico ainda passa. |

**Verificar o que está ligado hoje (PowerShell como administrador, somente leitura):**
`wsl --status`; `Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Windows-Subsystem-Linux,VirtualMachinePlatform,Microsoft-Hyper-V-All | Select FeatureName,State`;
`manage-bde -status C:`.

**Desativar WSL e Hyper-V (pede reinício):**
`dism.exe /online /disable-feature /featurename:Microsoft-Windows-Subsystem-Linux /norestart`,
`dism.exe /online /disable-feature /featurename:VirtualMachinePlatform /norestart`,
`dism.exe /online /disable-feature /featurename:Microsoft-Hyper-V-All /norestart`.
Quem tem acesso de administrador pode religar, então isto vale como atrito, não como trava. Não foi testado aqui.

## Ideias de próximos passos
- Regras AppLocker por hash/editor em vez de só caminho.
- Suporte a Firefox na instalação forçada de extensão (sistema diferente).

## Correções desta versão

- **Encerrado pelo Gerenciador de Tarefas não reabria**: o Watchdog agora mantém a reabertura *pendente* e repete
  até o app voltar (1ª tentativa pelo Agendador; depois, direto na sessão do usuário via `UserSessionLauncher`).
- **Aviso "foi para a bandeja" ao finalizar a tarefa**: "Finalizar tarefa" envia `WM_CLOSE`, que era tratado como o
  "X" da janela. Agora só o "X"/Alt+F4 (que chegam como `WM_SYSCOMMAND/SC_CLOSE`) escondem para a bandeja; um
  fechamento vindo de fora trava a tela por 30 min na hora.
- A penalidade por fechamento externo só vale com um gerenciador de processos aberto (evita falso positivo da miniatura da barra de tarefas/instaladores).
- Marca de "saída normal" antiga é apagada ao iniciar (`AppPresence.MarkStarted`).
- Renomeado ColdTurkeyClone → SharkBlocker (namespace, serviço `SharkBlockerWatchdog`, tarefa `SharkBlockerApp`,
  pasta `ProgramData\SharkBlocker`, registro, mutexes). Dados, serviço, tarefa e blocos do hosts do nome antigo são
  migrados/removidos automaticamente.

## Melhorias de bloqueio

- **Novo fundo** da janela principal: degradê vetorial + `App/Assets/bg-character.png` em tamanho nativo (320x625, sem ampliar).
- **Mais termos** em `BrowserExtension/adult-terms.js` (PT/EN/ES/DE/FR), revisados contra falsos positivos.
- **Termos fortes** (`CTC_STRONG_TERMS`) casam como trecho em qualquer parte da URL — pega domínios colados
  (`megaxvideosbr.com`) — e **TLDs adultos** (`CTC_ADULT_TLDS`: .xxx .porn .adult .sex).
- **Desofuscação** (`deobf.js` + `Deobfuscator.cs`, sempre espelhados): máscaras `p*rn`/`s#x`, entidades HTML
  (`&#112;orn`), `\u0070orn`, `%2570orn` (2 camadas), versaletes (`ᴘᴏʀɴ`), letras-emoji (🅿🅾🆁🅽 / 🇵🇴🇷🇳), mais
  homóglifos (cirílico/grego/armênio) e mais leet (`2`→z, `6`→g, `¢ § ¥ © µ`).
- Testes: `node tests/extension/run-tests.js` (216 casos, inclui falsos positivos conhecidos).

## Resistência a encerramento (app + Watchdog)

- O Watchdog, ao iniciar, considera "o app foi morto à força" se o batimento do app é de depois do boot do Windows
  (até 10 min) e não há saída normal registrada — não depende mais de uma janela de 30 s.
- O app vigia o Watchdog **sempre** (antes só com bloqueio/proteções permanentes), desde que já o tenha visto rodar.
- Tarefa agendada `SharkBlockerWatchdogRevive` (SYSTEM, 1/min) executa `sc start`: religa o serviço mesmo se for
  PARADO (sc stop / aba Serviços), caso em que o Windows não faz a recuperação automática. O Watchdog a recria se sumir;
  a desinstalação autorizada a remove primeiro.
- Termos mantidos a pedido: `r34`, `gozada`, `novinha`, `stripper`, `trepando`, `pron`.

## Persistência e sincronização de processos (App / Watchdog / Guard)

Três papéis, dois mecanismos nativos do Windows:

- **SharkBlocker.exe (App)** — interface e o "Guard": a vigília que o App mantém sobre o serviço.
- **Watchdog.exe (serviço do Windows, `SharkBlockerWatchdog`)** — aplica as proteções em segundo plano.
- **Guard** — não é um `.exe` separado: é a classe `App/Services/ProcessGuardian.cs`, rodando dentro
  do processo do App, que vigia o Watchdog (o Watchdog vigia o App na direção oposta, dentro de
  `EnforcementWorker.MonitorAppAsync`).

**1) Reinício automático pelo SCM.** `ServiceInstaller.Install` configura `sc failure` com
`actions= restart/5000/restart/5000/restart/5000` e `reset= 86400` (reinicia em 5 s por falha,
contador zera após 1 dia sem falha) e `sc failureflag 1` (a recuperação dispara mesmo numa parada
"inesperada" sem código de erro). Isso é reaplicado sozinho a cada ~60 s pelo próprio Watchdog
(`ServiceInstaller.EnsureFailureActionsConfigured`, chamado de `EnforcementWorker.ExecuteAsync`),
então revertê-lo pela aba "Recuperação" do `services.msc` não é permanente. O mesmo vale para as chaves de
modo seguro (`EnsureSafeBootRegistration`, ver a seção "Modo seguro").

**2) Recuperação de estado.** Se o App for encerrado sem a marca de saída normal
(`AppPresence`), `EnforcementWorker.MonitorAppAsync` trata como encerramento à força: ativa a trava
de tela (`StateManager.StartScreenLockIfNotActive`) se o modo ativo (bloqueio temporizado ou
proteções permanentes) estiver ligado, e reabre o App — primeiro pelo Agendador de Tarefas, depois
direto na sessão do usuário caso o Agendador falhe — até o processo voltar.

**3) `ProcessSync` (`App/Services/ProcessSync.cs`, compartilhado com o Watchdog) — Mutex + Named
Pipe.** Em vez de cada lado ficar rodando `sc query`/enumerando processos a cada checagem:
  - Cada lado segura um **Mutex nomeado global** (`Global\SharkBlocker_WatchdogAlive`,
    `Global\SharkBlocker_AppAlive`) do início ao fim da execução. O outro lado confirma presença
    com `Mutex.OpenExisting` — instantâneo, e o Windows fecha o objeto sozinho quando o processo
    dono morre (de qualquer forma), então a checagem nunca mente nem trava.
  - O Watchdog expõe um **Named Pipe** de status (`SharkBlocker_WatchdogStatus`, PING/PONG com um
    resumo JSON do estado atual). O Mutex sozinho não detecta um processo travado (de pé, mas em
    deadlock); o `ProcessGuardian` do App confirma com um ping no pipe antes de considerar o
    Watchdog "vivo de verdade". Falha sustentada (15 s) em qualquer uma das duas checagens é tratada
    como "Watchdog morto": trava a tela e reinicia o serviço (`ServiceInstaller.Start`).

## Bloqueio máximo (adulto)

- Restaurados e ampliados os termos (inclui `sex`/`sexo` avulsos nas BUSCAS, `chupando`, `mamada`, `orgia`, `topless`...),
  mais russo, japonês, italiano, espanhol, "IA de nudez/deepfake" e mais marcas. Termos CJK casam como trecho.
- Busca: agora são lidos TODOS os parâmetros de pesquisa (repetidos, com outro nome, em maiúsculas ou no `#fragmento`);
  o fragmento da URL também entra na análise.
- Texto "picado" sem separador (`p o r n h u b`, `po😀rn`): termos fortes (6+ letras) como trecho e busca inteira
  igual a um termo da lista.
- Leet em arte ASCII (`p()rn`, `n/\ked`, `|<ink`, `|\|ude`).
- TLDs: .xxx .porn .porno .adult .sex .sexy.
- Para afrouxar algo que bloqueie demais, basta apagar o termo em `BrowserExtension/adult-terms.js`.
