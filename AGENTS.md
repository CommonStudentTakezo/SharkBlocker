# AGENTS.md — guia para agentes de IA (SharkBlocker)

Leia **só este arquivo** primeiro.
> **Passagem de contexto entre conversas/modelos:** leia primeiro `docs/INSTRUCOES-PROXIMO-AGENTE.md`, depois `docs/HANDOFF-SHARKBLOCKER.md` (usuário, regras, estado dos dois apps, backlog). Endurecimento do ambiente: `docs/GUIA-ENDURECIMENTO.md`.
 Ele existe para você NÃO precisar abrir o projeto inteiro. Abra um
arquivo do código apenas quando a tarefa pedir uma mudança nele (use a tabela "Tarefa -> arquivo").

## O que é
Bloqueador de conteúdo/apps para Windows, de uso pessoal (autocontrole). Dois executáveis .NET 8
(`net8.0-windows`, self-contained, win-x64) + extensão de navegador (Manifest V3, JS):

- `App/` -> `SharkBlocker.exe`: interface WPF (+ WinForms só para o ícone da bandeja), trava de tela,
  vigia da barra de endereço, o "Guard" que vigia o Watchdog. Exige administrador.
- `Watchdog/` -> `Watchdog.exe`: serviço do Windows (LocalSystem, sessão 0, sem tela). Reforça hosts,
  políticas e extensão; vigia o App; reabre o App e trava a tela se ele for finalizado à força.
- `BrowserExtension/`: bloqueio por URL/palavra-chave dentro do navegador.
- Scripts na raiz: `quick-install.ps1` (publica e instala, transacional), `install-service.ps1`,
  `uninstall-service.ps1`, `health-check.ps1`, `protection-pause.ps1`, `pause-gate.ps1`,
  `emergency-fix-hosts.bat`, `Installer.iss` (Inno Setup).

Idioma do projeto: **português (BR)** em comentários, logs, UI e docs. Mantenha.

## O que NÃO ler (custa caro e não ajuda)
- `bin/`, `obj/` (saída de compilação; podem nem existir no zip). Nunca edite.
- `README.md` (~52 KB, é um histórico de rodadas, com seções repetidas). Não leia inteiro: use
  `grep -n "^## " README.md` e abra só a seção do assunto.
- `Assets/*.png|jpg|ico`, `ELLEN-ASSETS.md` (só enfeite do mascote).
- `ExtensionSigningKey/extension_key.pem`: chave PRIVADA. Nunca abra, imprima ou cole em respostas.
- Arquivos grandes: use `grep -n` e `sed -n 'A,Bp'` em vez de ler tudo:
  `App/MainWindow.xaml.cs` (~1700 linhas), `ScreenLockWindow.cs` (650), `UrlPolicy.cs` (600),
  `ExtensionInstaller.cs` (600), `StateManager.cs` (590), `HostsFileManager.cs` (500).

## Leitura útil e curta
- `ARCHITECTURE.md` (6 KB): modelo de senha, quem vigia quem, scripts, limites. Vale ler se a tarefa
  tocar em Watchdog, Guard, trava, senha ou instalação.

## Mapa: tarefa -> arquivo
| Tarefa | Onde mexer |
|---|---|
| Sites bloqueados no hosts, ACL do hosts | `App/Services/HostsFileManager.cs` (+ `HostsWatcher.cs`) |
| Apps/processos bloqueados | `App/Services/ProcessBlocker.cs` (listas e kill), `ProcessStartWatcher.cs` (WMI em tempo real) |
| Regras de URL / termos proibidos | `App/Services/UrlPolicy.cs` **e** `BrowserExtension/background.js`+`matcher.js` (espelhos, ver abaixo) |
| Listas de termos | `BrowserExtension/adult-terms.js`, `proxy-terms.js` (lidas por `TermLists.cs`) |
| Desofuscação ("p0rn", letras russas...) | `App/Services/Deobfuscator.cs` **e** `BrowserExtension/deobf.js` (mudar os dois) |
| Barra de endereço do navegador (desktop) | `App/Services/BrowserUrlWatcher.cs` (UI Automation, ciclo 1,5 s) |
| Trava de tela (visual, hook de teclado; trava do bloqueio agendado; layout da Ellen) | `App/Dialogs/ScreenLockWindow.cs` (`ShowScheduledBlockIfDue`, `LockWindow` ctor/`SizeChanged`), `MotivationalQuotes.cs` |
| Fechou à força -> trava 30 min + reabrir | `Watchdog/EnforcementWorker.cs` (`MonitorAppAsync`, `LaunchApp`), `App/Services/AppPresence.cs`, `UserSessionLauncher.cs` |
| Watchdog morto/congelado | `App/Services/ProcessGuardian.cs`, `ProcessSync.cs` (mutex + named pipe), `ServiceInstaller.cs` |
| Modo seguro (chaves SafeBoot, `SM_CLEANBOOT`) | `ServiceInstaller.cs` (`EnsureSafeBootRegistration`, `GetCleanBootMode`), `Watchdog/EnforcementWorker.cs` (`StartAsync`, `EnforceImmediately`), `ProcessStartWatcher.cs` |
| VPN/proxy: extensões (`ExtensionSettings`), programas, VPN nativa | `BrowserPolicyManager.cs` (`EnsureProxyPermissionBlocked`/`RemoveProxyPermissionBlock`), `ProcessBlocker.cs` (listas + `OptionalVpnProcessNames`), `AppLockerManager.cs`, `NativeVpnGuard.cs` (rasdial), `BlockState.BlockOptionalVpnTools` + botão na aba Proteções |
| Tradutor/cache/arquivo web, IP literal, DoH por nome (U7) | `BrowserExtension/proxy-terms.js` (`gatewayServices`, `archiveServices`, `dohHosts`), `matcher.js` (`serviceMatches`, `isPublicLiteralIp`), `background.js` (`evaluateGateway`), espelho em `UrlPolicy.cs`, `SafeSearchDomains.cs` (DoH no hosts), `BlockState.BlockWebArchives` + botão na aba Proteções |
| Auditoria de contorno (achados U1–U8 e plano) | `docs/AUDITORIA-CONTORNO.md` |
| Estado, assinatura HMAC, trava no Registro | `App/Services/StateManager.cs`, `Models/BlockList.cs`, `AtomicFile.cs` |
| Senha / tentativas | `PasswordGate.cs`, `PasswordManager.cs`, `PasswordDialog.xaml(.cs)` |
| Desinstalar / confirmações | `App/UninstallFlow.cs`, `ConfirmationGauntlet.cs`, `Dialogs/GauntletWindow.*` |
| Extensão instalada à força (Chrome/Edge/Brave) | `ExtensionInstaller.cs`, `BrowserPolicyManager.cs` |
| SafeSearch / DoH | `SafeSearchDomains.cs`, `BrowserPolicyManager.cs` |
| Proteção da pasta de instalação | `FolderProtection.cs` |
| Abrir no login (Agendador) | `AppStartupManager.cs` |
| Servidor local p/ a extensão | `LocalStateServer.cs` (somente leitura, `127.0.0.1:57411/state`) |
| Janela principal, bandeja, indicador de saúde | `App/MainWindow.xaml(.cs)` |
| Bloqueios agendados (recorrentes, ex.: todo dia 22h–6h) | `App/Services/BlockScheduler.cs` (compartilhado com o Watchdog), `App/ScheduleEditorWindow.*`, aba Agenda em `MainWindow`, `UninstallFlow.TryAuthorizeScheduleChange` |
| Falas da Ellen que reagem a eventos (busca barrada, bloqueio cumprido) | `App/Services/EllenEventService.cs`, `DesktopQuoteWindow.React`, `ScreenLock.TextFor` |
| Widget da Ellen: arrastar, posição salva, modo compacto, multi-monitor | `Dialogs/DesktopQuoteWindow.*`, `Services/EllenWidgetSettings.cs` (estado + geometria, sem UI), `EllenQuoteCard.Compact` |
| Mascote Ellen (frases, widget) | `Controls/EllenQuoteCard.*`, `Dialogs/DesktopQuoteWindow.*`, `Ellen*Service.cs` — só enfeite |
| Sequência e estatísticas (aba Status, resumo semanal da Ellen) | `App/Services/StatsStore.cs` (compartilhado com o Watchdog), `MainWindow.RefreshStatsAsync`, `EllenChatterService.Pick` |
| Instalação / publicação | `quick-install.ps1`, `install-service.ps1`, `Installer.iss`, `*.csproj` |

## Armadilhas que já custaram caro
1. **O Watchdog compila arquivos do App.** `Watchdog/Watchdog.csproj` tem `<Compile Include="..\App\...">`
   para `StateManager`, `HostsFileManager`, `ProcessBlocker`, `AppPresence`, `ProcessSync`, `Logger`,
   `AtomicFile`, `BrowserPolicyManager`, `ExtensionInstaller` etc. Mudar esses arquivos afeta os DOIS
   executáveis. Se um arquivo compartilhado passar a usar um tipo novo de outro arquivo, adicione o
   `Compile Include` correspondente no `Watchdog.csproj`. Não use `System.Windows.*` (WPF) nesses arquivos
   (o Watchdog não referencia WPF); `ProcessBlocker.cs` já é compartilhado: mantenha-o livre de UI.
2. **Espelhos C# <-> JS.** `UrlPolicy.cs` espelha `background.js`/`matcher.js`; `Deobfuscator.cs`
   espelha `deobf.js`. Mudou um lado, mude o outro. Teste da extensão: `node tests/extension/run-tests.js`.
3. **Named pipe / mutex com DACL via P/Invoke** (não use o pacote `System.IO.Pipes.AccessControl`: quebra
   o restore com NU1102). Ver comentário em `App.csproj`.
4. **Senha é fail-closed**: sem senha definida, ações críticas são RECUSADAS. PBKDF2-SHA256, 600.000
   iterações; hash espelhado e assinado no Registro. Não afrouxe.
5. **Estado em dois lugares** (JSON em ProgramData + Registro HKLM\SOFTWARE\SharkBlocker), vale o maior
   horário com HMAC válido. Todo acesso ao `state.json` passa por `StateManager` (mutex global).
6. **Não faça `icacls.exe` à toa.** `HostsFileManager.Protect/Unprotect` já checam o estado antes
   (o Watchdog os chama a cada 5 s). Não reintroduza chamadas incondicionais.
7. **Não use `Process.MainModule`** para ler caminho de executável: use `ProcessBlocker.GetImagePath(pid)`.
8. **Não liste todos os processos num laço de 1 s.** `EnforcementWorker.IsAppRunning()` segura o handle
   do app de propósito.
9. **Nunca** coloque `SharkBlocker`, `Watchdog`, `explorer`, `svchost`, `lsass` etc. na lista de
   processos bloqueáveis (`ProcessBlocker.NeverKill`).
10. **O `App.csproj` tem WPF + WinForms**: o SDK NÃO inclui `System.IO` (nem `System.Net.Http`) nos usings
    implícitos. Todo arquivo do App que usar `Path`, `File`, `Directory`, `FileInfo`... precisa de
    `using System.IO;` explícito. (O Watchdog não tem esse problema.) Já deu erro CS0103 por isso.
11. Fechamento "normal" do app só é válido com `AppPresence.MarkCleanExit` (senha + confirmações).
    Instalar/atualizar usa o marcador `planned-restart.flag` (reabre sem travar).

## Ritmos (não mude sem entender o efeito)
Watchdog vigia o App: 1 s · ciclo de enforcement do Watchdog: 5 s (pasta/tarefas/`sc failure`: a cada
12 ciclos ≈ 60 s) · Guard vigia o Watchdog: 3 s (pipe: 15 s) · BrowserUrlWatcher: 1,5 s ·
batimento do app: máx. 1 gravação por 4 s · indicador de saúde: 60 s · varredura de processos no App:
a cada ~3 s só enquanto há bloqueio.

## Compilar e testar
- Só compila no Windows com SDK .NET 8. Em ambiente Linux/sem .NET você NÃO consegue validar a build:
  diga isso na resposta, não afirme que compilou.
- Publicar: `dotnet publish App -c Release -r win-x64 --self-contained true` (idem `Watchdog`), ou
  `./quick-install.ps1` (sem `-SkipPublish` para recompilar). Admin necessário.
- Diagnóstico: `health-check.ps1` (somente leitura). Log: `C:\ProgramData\SharkBlocker\app.log` (`Services/Logger.cs`).
- Extensão: `node tests/extension/run-tests.js` (sem navegador).
- Depois de mudar a extensão, aumente `version` em `BrowserExtension/manifest.json` (atual 1.10) para o
  Chrome atualizar; o `.crx` é regenerado pelo `ExtensionInstaller` com a chave em `ExtensionSigningKey/`.

## Como trabalhar aqui (para poupar tempo)
1. Descubra o arquivo pela tabela acima; `grep -n` o símbolo; leia só o trecho.
2. Edite com mudança mínima, mantendo comentários em português e o estilo existente.
3. Se tocar em arquivo compartilhado com o Watchdog, confira o `Watchdog.csproj`.
4. Entregue só os arquivos alterados MAIS indicação de onde extrair; se o usuário não souber, entregue o
   projeto completo (sem `bin/` e `obj/`). Nunca entregue um zip parcial sem avisar.
5. Registre mudanças relevantes em uma seção curta no fim deste arquivo (abaixo), não no README.

## Histórico de otimizações já feitas (não refazer)
- `HostsFileManager`: Protect/Unprotect idempotentes (leem atributos + ACL antes de chamar `icacls`).
- `ProcessBlocker`: caminho do exe via `QueryFullProcessImageName` (`GetImagePath`) em vez de `MainModule`.
- `BrowserUrlWatcher`: nome do processo pelo caminho; elemento da barra de endereço em cache.
- `EnforcementWorker`: `IsAppRunning()` com handle em cache.
- `.csproj`: `SatelliteResourceLanguages=pt-BR`; Watchdog com GC não concorrente.
- Não feito de propósito: cache/consolidação de leituras de Registro em `StateManager.Load()` (risco no
  núcleo de autenticação, ganho pequeno); ReadyToRun (exige pacotes extras no publish).

## Registro de mudanças
- Sequência e estatísticas: `StatsStore.cs` grava `ProgramData\SharkBlocker\stats.json` (fora do state.json assinado).
  Eventos entram em `StateManager.StartScreenLockIfNotActive` (motivo `search` = busca barrada; `closed`, `tamper`,
  `uninstall`, `removal` = tentativa de burlar, zera a sequência; dedup de 60 s). Tempo bloqueado: o Watchdog soma
  por ciclo (`TickBlocked`/`EndBlocked`). Arquivo compartilhado: tem `Compile Include` no `Watchdog.csproj`.
  Aba Status: cartão no topo. Ellen: resumo semanal (1x a cada 7 dias) em `EllenChatterService.Pick`.
- Falas por evento: `EllenEventService`. Busca barrada: "Peguei essa." abre o texto da trava (`ScreenLock.TextFor`),
  porque o widget fica coberto por ela. Bloqueio cumprido: `OnTick` (chamado em `MainWindow.Timer_Tick`) detecta o fim
  por tempo e chama `DesktopQuoteWindow.React`; remoção antecipada (senha) não comemora; respeita Modo Silêncio e trava.
- Bloqueios agendados: `BlockState.Schedules` + `SchedulesConfigured`. `BlockScheduler.EnsureActive()` grava o fim da janela em
  `LockedUntilUtc` (mesmo bloqueio do início manual); chamado pelo Watchdog a cada ciclo e pelo app a cada ~10 s. Janelas
  encadeadas viram um bloqueio contínuo (máx. 48 h). Primeira configuração livre; depois disso, mudar exige trava de tela de
  30 min (motivo `schedule`, não conta nas estatísticas) + senha (`TryAuthorizeScheduleChange`). Arquivo compartilhado: tem
  `Compile Include` no `Watchdog.csproj`.
- Modo seguro (auditoria U1): `ServiceInstaller` grava/reafirma `SafeBoot\Minimal|Network\SharkBlockerWatchdog` (valor padrão `Service`,
  idempotente, chamado no bloco de ~60 s do `EnforcementWorker` junto do `sc failure`) e remove só na desinstalação (a pausa NÃO remove).
  `EnforcementWorker.StartAsync` lê `SM_CLEANBOOT`, loga e chama `EnforceImmediately`; `LocalStateServer`, `HostsWatcher`, `ProcessSync`
  e `ProcessStartWatcher` (que não inicia o WMI no modo seguro) falham com tolerância. `ServiceInstaller` já era compartilhado: sem novo
  `Compile Include`. `install-service.ps1` e `health-check.ps1` também tratam as chaves. Não validado em Windows real (ver README).
- Widget flexível: `EllenWidgetSettings` grava `ProgramData\SharkBlocker\ellen-widget.json` (compacto, monitor, canto TL/TR/BL/BR
  + distâncias em DIPs; não é compartilhado com o Watchdog). Posição presa ao canto mais próximo da ÁREA ÚTIL do monitor
  (resiste a mudança de resolução/barra de tarefas); monitor ausente = usa o principal sem apagar o salvo. Posicionamento em
  pixels via `SetWindowPos` (mesmo espaço de `Screen.*` da trava de tela). NÃO mudei o `app.manifest` (DPI): a trava de tela
  depende do modo atual. Compacto: `Card.Compact` esconde o balão; se a Ellen fala, expande só durante a fala
  (`_tempExpanded`). Botão direito no widget = menu (WinForms `ContextMenuStrip`, como a bandeja). Não validado em build.
- VPN e proxy (auditoria U2): `ExtensionSettings` mesclado (`"*".blocked_permissions=["proxy"]`, só na camada permanente, idempotente; o marcador
  em `HKLM\SOFTWARE\SharkBlocker\PolicyMarkers` diz o que é nosso, e só isso é removido). `ProcessBlocker` leu a opção `BlockOptionalVpnTools`
  via `StateManager.Load()` (desligada = lista opcional fora). `NativeVpnGuard.cs` é compartilhado: tem `Compile Include` no `Watchdog.csproj`.
  Sem lista de IDs de extensão. Não validado em build nem em Windows real (ver README).
- Trava no bloqueio agendado: `ScreenLock.ShowScheduledBlockIfDue(state)` (chamado em `App.OnStartup` e `MainWindow.Timer_Tick`) trava até `BlockScheduler.ActiveUntilUtc`
  (recalculado, sem persistência; teto `ScheduledMaxDuration` = 72 h; `ShowUntil` ganhou o parâmetro opcional `maxDuration`). Contagem H:MM:SS a partir de 1 h.
  Layout: o palco da Ellen reserva espaço dos dois lados do painel (`fit.MaxWidth`) e o painel fica num `Viewbox` DownOnly. `ellen-angry.png` agora é corpo inteiro. Não validado em build.
- Tradutor, cache, IP literal e DoH (auditoria U7): `proxy-terms.js` ganhou `gatewayServices` (tradutor/cache), `archiveServices` (arquivo web, só com a opção
  `BlockState.BlockWebArchives`, DESLIGADA, botão na aba Proteções; a extensão lê `blockWebArchives` em `/state`) e `dohHosts` (vai para o hosts via
  `SafeSearchDomains.GetHostsEntries`, camada permanente). Formato das duas primeiras: `"host"` ou `"host/prefixo"` (`".*"` no fim do host = sufixo de país); sem `//` nem
  `]` nas entradas (o `TermLists.cs` lê o arquivo com regex simples). `matcher.js` ganhou `serviceMatches`/`isPublicLiteralIp`; `background.js`, DEPOIS do destino
  embutido e só com `locked`, devolve `gateway` ou `ip`. Esses dois motivos NÃO estão em `LOCK_REASONS`/`UrlPolicy.LockReasons` (não travam a tela; o vigia de
  desktop, que só age nos motivos de trava, os ignora). Espelho em `UrlPolicy.cs` (`ServiceMatches`, `IsPublicLiteralIp`). Extensão passou para 1.10.
  Não validado em build (sem .NET no ambiente) nem em Windows real (ver README).
- Paridade e diagnóstico (2026-10): `tests/parity/` (`generate-cases.js` define casos com intenção e grava `cases.json`; `run-parity.js` confere a extensão; 128 casos OK em Node) compartilhado com o app Android (Kotlin) e com `tests/SharkBlocker.ParityTests` (xUnit, referencia o App; **não compilado**; só casos de URL porque `ServiceMatches`/`IsPublicLiteralIp` são `internal`). `diagnostico.ps1` gera relatório sem senha/listas/conteúdo (ASCII; copia para a área de transferência). A paridade já achou uma divergência (profundidade de URL embutida: Kotlin 3, JS/C# 2; corrigida no Kotlin).
- Rodada 2026-10-29: **trava = 30 min** via `StateManager.ScreenLockPenalty` (não use mais o literal `TimeSpan.FromMinutes(15)`; o Watchdog não enxerga `ScreenLock`, por isso a constante mora no `StateManager`). Ellen **tsundere** (marra por fora, cuida por dentro, nunca humilha; frases neutras de gênero em relação ao usuário). Sorteio de falas e cenas por `ShuffleBag.Next(chave, lista)`. Expressões: calm, determined, sweet, warm, smirk e **shy** (nova). 19 rostos limpos + `ellen-angry.png` sem halo (ver `ELLEN-ASSETS.md`). Não compilado: confira com `dotnet build` e `.\health-check.ps1`.

- Rodada 2026-11-01: **botão de urgência** (#26): `ScreenLock.ReasonUrgency` ("urgency"), 30 min sem senha, cena suave (`_gentle`: sem Ellen brava), item na bandeja e botão "Preciso de uma pausa" (`MainWindow.StartUrgency`). Motivo fora do `StatsStore.KindFor`, então não zera a sequência. Corrigido o teto `ScreenLock.MaxDuration` (era 20 min e cortava a trava de 30; agora `ScreenLockPenalty` + 15 min). Removida uma frase duplicada da frase do dia. **Não compilado.**
- Rodada 2026-11-01 (2): **sequência menos "tudo ou nada"** (#4, só PC): `StatsStore.CleanDays` (dias locais sem tentativa de burlar na janela de 30, contando só dias desde o início) em `StatsSummary.CleanDays30/TotalDays30`; aparece na aba Status ("Dias limpos (últimos 30)") e no resumo semanal após tropeço. O recorde (`BestStreakDays`) já sobrevivia ao reinício. Testes xUnit em `tests/SharkBlocker.ParityTests/StatsTests.cs`. O build da rodada anterior (urgência) foi confirmado pelo usuário sem erros; este item **não compilado**.
- 2026-11-01 (3): usuário confirmou build limpo do App. `dotnet test` falhava só porque o PC dele tem apenas o runtime .NET 10 (testhost pedia o 8): `RollForward=LatestMajor` no `SharkBlocker.ParityTests.csproj` (só testes; o App é self-contained e não muda).
- Rodada 2026-11-01 (4): **VMs/WSL/Sandbox** (#11, U8): opção `BlockState.BlockVirtualization` (desligada por padrão; ligar sem senha, desligar com `UninstallFlow.TryAuthorizeRemoval`), lista `ProcessBlocker.VirtualizationProcessNames` (só nomes exatos; sem serviços, sem `bash`/`msrdc`) concatenada em `EffectiveNames` junto com a das VPNs opcionais (o antigo `OptionalVpnToolsEnabled` foi removido). Card na aba Proteções (`VirtualizationButton`). Limite: o Windows pode recusar encerrar `vmmem`. **Não compilado.**
- Rodada 2026-11-01 (5): **firewall contra VPN/proxy/Tor** (#10): `App/Services/FirewallBlocker.cs` (compilado também pelo Watchdog; SEM WPF). Regras `netsh` de SAÍDA bloqueada por executável (`SharkBlocker-FW-<hash>`), só com bloqueio ativo: caminhos que o `ProcessBlocker` acabou de encerrar (`RecordKilledPath`/`TakeKilledPaths`) + pastas de VPNs conhecidas (descoberta 1x por bloqueio). Watchdog aplica no ciclo com bloqueio e `RemoveIfActive()` quando não há; `UninstallFlow` e `emergency-fix-hosts.bat` fazem limpeza profunda (por nome, PowerShell). Registro dos nomes em `ProgramData\SharkBlocker\firewall-rules.txt`. Nunca regra genérica de rede; nunca caminho do Windows. Limite: Firewall do Windows desligado = sem efeito. **Não compilado nem testado.**
- Rodada 2026-11-01 (6): **filtro de DNS local** (#9): `App/Services/LocalDnsFilter.cs` (`DnsFilterCore` puro e testável + servidor UDP/TCP em 127.0.0.1/::1:53; NXDOMAIN para a lista (com subdomínios) e para nomes canário `use-application-dns.net`/`mask*.icloud.com`; o resto vai ao Cloudflare Família 1.1.1.3/1.0.0.3) e `DnsFilterController.cs` (compilados também pelo Watchdog; SEM WPF). Opção `BlockState.LocalDnsFilterEnabled` (desligada; ligar sem senha com confirmação, desligar com `TryAuthorizeRemoval`). Só ativo com opção ligada E (bloqueio ativo OU proteções permanentes). Ordem de segurança: sobe o servidor → autoteste (`LocalDnsFilter.Probe`) → só então troca o DNS (backup em `ProgramData\SharkBlocker\dns-backup.json`: estático ou DHCP) e cria as regras `SharkBlocker-DNS-*` (DoT 853, DoH 443 para IPs conhecidos, porta 53 para todos exceto 127/8, ::1 e 1.1.1.3/1.0.0.3). FAIL-OPEN: 2 autotestes seguidos falhando desfaz tudo. Desfaz também ao desligar a opção, fim do bloqueio, `Shutdown()` no `StopAsync`, `UninstallFlow` (`RestoreSystemDns`) e `emergency-fix-hosts.bat`. `health-check.ps1` ganhou a seção 7. Testes: `DnsTests.cs` (parte pura). **NADA disto foi compilado nem testado em Windows**: peça teste real com a opção ligada.
- Rodada 2026-11-01 (7): **mapa de horários de risco** (#6, só PC): `StatsStore.HourHistogram`, `SuggestWindow` (melhor janela circular de 3 h, estendida até 6 h por vizinhos >= 1/3 do pico, só se cobrir >= 40% e houver >= 5 ocorrências), `FormatRiskMap`; aba Status mostra o mapa em barras e o botão "Criar agenda sugerida", que abre `ScheduleEditorWindow` com a linha nova (passa pela mesma trava + senha de qualquer mudança de agenda; só vale se salvar). Testes em `StatsTests.cs`. **Não compilado.**
- Exceção fixa para o repositório do classificador (2026-11-02, extensão 1.11): `github.com/GantMan/nsfw_model` casava o termo `nsfw`
  (palavra inteira; `_` separa) e travava a tela por 30 min. `background.js` (`CTC_ADULT_EXCEPTIONS` + `neutralizedHead`) e
  `UrlPolicy.cs` (`AdultExceptions` + `NeutralizedHead`) neutralizam **só** o trecho exato `host/dono/repositório` antes das checagens;
  subpastas, `?q=`, fragmento e qualquer outro endereço com `nsfw` continuam barrados. Não há tela para editar a exceção (só código).
  Testes: seção "EX" em `tests/extension/run-tests.js` (403 OK) e 12 casos novos em `generate-cases.js` → `cases.json` (140 OK).
  O lado C# **não foi compilado**. O app Android tem a mesma exceção em `UrlPolicy.kt` (`ADULT_EXCEPTIONS`): mantenha os dois iguais.
  Barra de endereço (2026-11-02): `BrowserUrlWatcher` lê o texto da omnibox a cada 1,5 s, inclusive o que ainda está sendo digitado;
  por isso usa `UrlPolicy.EvaluateTyped`, que também aceita um *começo* de `github.com/gantman/nsfw_model` (sem isso, digitar o
  endereço travava a tela em `.../nsfw`). Só C#, sem espelho em JS (a extensão só vê a navegação já enviada). Não compilado.
  Arquivos de release (2026-11-02): dentro do repositório liberado, a sigla `nsfw` no *resto do caminho* (ex.: `/releases/download/1.1.0/nsfw_mobilenet2.224x224.h5`)
  também é neutralizada (`nsfw`→`modelo`); `porn`, `hentai` etc., a busca e o fragmento continuam avaliados. Mantenha JS, C# e Kotlin iguais.
  Segunda exceção (2026-11-02): `s3.amazonaws.com/ir_public/nsfwjscdn` (pasta pública de onde o repositório baixa `nsfw_mobilenet2.224x224.h5`), mesma regra
  (só esse host/pasta; `nsfw` no resto do caminho vira `modelo`; `porn` etc., busca e fragmento continuam valendo; `ir_public.s3...`, outro bucket e `nsfwjscdn_extra` seguem barrados).

## Rodada de 2026-11-03 (S3 do modelo; extensão 1.12)
- **Confirmado pelo usuário:** os filtros de rede do PC (DNS local + firewall) bloqueiam tudo durante uma trava.
- **Falha:** acessar `s3.amazonaws.com/ir_public/nsfwjscdn` travava o PC. A 2ª exceção estava em `background.js`, mas o `manifest.json` ficou em 1.11 e o Chrome não atualiza sem a versão subir. **Regra: mudou algo em `BrowserExtension/`, suba `version`.** Agora **1.12**.
- Exceções endurecidas nos três (`neutralizedHead` JS · `NeutralizedHead`/`CanonExceptionHost` C# · `neutralizedHead` Kotlin): host regional do S3 (`s3.us-east-1...`, `s3-us-west-2...`, dualstack) vale como `s3.amazonaws.com`; caminho comparado decodificado (`%2F`) e sem `//`. Seguem barrados de propósito: `?prefix=nsfwjscdn`, `ir_public.s3.amazonaws.com`, outro bucket/pasta, host parecido (`...amazonaws.com.evil.example.com`).
- Testes: `run-tests.js` 441 OK; `cases.json` 174 casos (JS verificado; **C# e Kotlin não compilados**). Rode `dotnet test` e `./gradlew test`.
- Removida a pasta solta `App/src/` (continha `UrlPolicy.kt`/`PortTests.kt` do Android por engano).

## CI (#19) — 2026-11-04
- `.github/workflows/ci.yml`: job **js** (ubuntu: `run-tests.js`, `run-parity.js`, `generate-cases.js` + `git diff --exit-code` em `cases.json`, e `tests/ci/check-extension-version.js`, que **reprova se `BrowserExtension/` mudou e a `version` do manifest não subiu**) e job **dotnet** (windows: `dotnet build SharkBlocker.sln -c Release` + `dotnet test tests/SharkBlocker.ParityTests -c Release`).
- **Sem GitHub:** `verificar-tudo.ps1` (raiz do projeto) roda as mesmas etapas localmente (Node, cases.json, versão da extensão via `tests/ci/check-extension-version-local.js` — sem git, guarda a impressão digital em `tests/ci/.extension-state.json` —, build Release e `dotnet test`). **Não testado em Windows.** Ao entregar patch que mexe na extensão, o agente deve subir a `version` e pedir ao usuário que rode esse script.
- Novo `.gitignore` (inclui `*.pem`: a chave privada da extensão **nunca** vai ao repositório).
- Pasta do projeto = raiz do repositório. **Não executado ainda** (precisa subir ao GitHub); o verificador de versão foi testado localmente num repositório de mentira. Se o primeiro run falhar, corrija o workflow antes de qualquer função nova.

## Incidente de DNS (2026-11-08): sites com erro de DNS até rodar o emergency-fix
- **Sintoma (relato do usuário):** com o filtro de DNS local, os sites dão erro de DNS (`DNS_PROBE_FINISHED_*`, o usuário citou a variante "...DOMAIN", provavelmente NXDOMAIN; **confirmar o nome exato**) e só voltam depois do `emergency-fix-hosts.bat`. **Causa NÃO identificada.** No código, NXDOMAIN só sai (a) da lista de bloqueio com bloqueio ativo ou (b) do servidor de cima; domínios de um rótulo só (ex.: "com") são impossíveis (`DomainSyntax` exige ponto).
- **Brecha real corrigida (não compilada):** "DNS órfão" — Windows apontando para 127.0.0.1/::1 sem o filtro atendendo (serviço morto à força, reinício sem internet, autoteste falhando, backup perdido): antes só o emergency-fix resolvia. Agora `DnsFilterController.HealOrphan` (a cada 30 s, só se o filtro não está atendendo, checando os adaptadores via .NET e fazendo um `Probe`) devolve o DNS ao normal. Se outro programa responde em 127.0.0.1:53 (Pi-hole, AdGuard Home), não mexe.
- **Diagnóstico:** `LocalDnsFilter.UpstreamFailures` + linha no log a cada 5 min ("N consultas, M bloqueadas, K sem resposta do servidor de cima"); novo `diagnostico-dns.ps1` (somente leitura; roda **enquanto está quebrado**, antes do emergency-fix).
- **Próximo passo:** pedir o `diagnostico-dns.txt` e o nome exato do erro. Suspeitas ainda abertas: DNS seguro (DoH) personalizado no Chrome/Edge, rede que bloqueia 1.1.1.3:53, conflito na porta 53 (hotspot/ICS/WSL/Docker), reinício com o DNS persistido.

### Causa encontrada (2026-11-09, pelo `diagnostico-dns.txt` do usuário)
- Evidência: Wi-Fi com DNS 127.0.0.1/::1, **nada escutando a porta 53**, **sem regras de firewall**, **serviço inexistente**, `dns-backup.json` ainda no disco; log termina em "desativando (serviço parando)" sem "desativado". Ou seja: o serviço foi parado/reinstalado e **o Watchdog foi morto no meio da restauração** (firewall já removido, DNS ainda não).
- Causa: `ServiceInstaller.Uninstall()` esperava só ~5 s após `sc stop` e depois `delete` + `KillAllWatchdogProcesses()`; `StopAsync` → `DnsFilterController.Shutdown()` roda 6× `netsh` + PowerShell (vários segundos). Sem serviço, nada consertava. Isso também explica o relato original ("só funciona depois do emergency-fix"): acontecia a cada reinstalação/atualização.
- Correções (não compiladas): (1) `Shutdown` restaura o **DNS primeiro** (`Deactivate(..., dnsFirst: true)`) e loga "desativado" ao terminar; (2) `Uninstall` espera até ~30 s; (3) `KillAllWatchdogProcesses` chama `DnsFilterController.RepairIfOrphaned` se matou algum; (4) o App chama `RepairIfOrphaned` ao abrir (como administrador). `RepairIfOrphaned` não faz nada se alguém atende em 127.0.0.1:53 ou se um Watchdog vivo está com o DNS ok.
- Teste sugerido: com o filtro ativo (bloqueio ou proteções permanentes ligados), reinstalar o serviço (botão do app) e conferir que o DNS volta ao normal (`diagnostico-dns.ps1`, seção 1) e que o log tem "desativado".

## Palavra "biquíni/bikini" (2026-11-09, extensão 1.13)
- Pedido do usuário: bloquear "biquini" e variações no PC e no Android. Entram em `CTC_URL_TERMS` (`adult-terms.js`, fonte única): `biquini, biquíni, biquinis, biquínis, bikini, bikíni, bikinis, bikínis`. Palavra inteira, sem acento/maiúscula, vale na URL e nas buscas; a desofuscação pega `B1QUIN1`. O Android usa uma **cópia** em `assets/terms/adult-terms.js` (mantenha idêntica) e a mesma lista de casos em `cases.json`.
- Limites conhecidos: palavra inteira, então **domínio colado** (`biquinistore.com`) não casa (bloqueie o domínio na lista de sites); falsos positivos esperados (Atol de Bikini, Bob Esponja "Bikini Bottom", banda Bikini Kill). Subir a `version` da extensão foi necessário (1.13).
- Testes: `run-tests.js` 464 OK; paridade 197 casos.

## Playboy e nomes de atrizes/atores adultos (2026-11-10, extensão 1.14)
- Entraram em `CTC_URL_TERMS` (`adult-terms.js`): frases da marca (`playboy magazine`, `playboy com`, `revista playboy`, `playboy brasil`, `playmate of the month`...) e ~55 nomes completos de atores/atrizes adultos. Cópia em `assets/terms/` do Android; casos novos em `generate-cases.js` (cases.json regenerado e recopiado).
- **`playboy` sozinho NÃO está na lista de propósito** (Playboy Carti, uso como adjetivo). Para barrar tudo, acrescente `"playboy"` à lista. Só nomes completos, nunca primeiro nome; nomes de celebridades de TV/cinema que posaram uma vez ficaram de fora de propósito (bloqueariam notícias comuns).
- Limites: palavra inteira (domínio colado, como `rileyreid.com`, não casa); homônimos inocentes com o mesmo nome completo serão barrados. Lista incompleta por natureza: o usuário pode acrescentar nomes à mão.

## Detector de imagens do PC (2026-11-10, NÃO compilado)
- `App/Services/ImageGuard.cs` (laço de 2 s, captura de cada monitor; toda detecção acima do limite trava em escada 5 → 15 → 30 min via `StateManager.StartImageScreenLock`, contagem em `BlockState.ImageStrikes`, zera após 24 h) e `App/Services/NsfwClassifier.cs` (ONNX Runtime, `Microsoft.ML.OnnxRuntime` 1.18.1 no `App.csproj`). Opção `BlockState.ImageDetectorEnabled`/`ImageSensitivity` (desligada por padrão; ligar sem senha, desligar com `UninstallFlow.TryAuthorizeRemoval`). Motivo de trava `image` (`ScreenLock.ReasonImage`; conta como "search" em `StatsStore.KindFor`).
- Modelo: `scripts/converter-modelo-onnx.py` (não testado) grava `App/Assets/nsfw/model.onnx` + `model.json`; o `.csproj` copia para `nsfw\` ao lado do .exe. Detalhes e limites: `docs/DETECTOR-DE-IMAGENS-PC.md`.
- **Não inclua `ImageGuard.cs`/`NsfwClassifier.cs` em `Watchdog.csproj`** (WinForms/ONNX; o Watchdog roda na sessão 0 e não vê a tela). Se o build falhar, o suspeito nº 1 é a API do ONNX Runtime ou a restauração do pacote NuGet.

## Sites sempre bloqueados, só Shorts no YouTube e YouTube Music liberado (2026-11-11, extensão 1.16, NÃO compilado)
- **Lista permanente:** `CTC_PERMANENT_SITES` em `BrowserExtension/adult-terms.js` (108 domínios; lida por C# `TermLists.PermanentSites()` e Kotlin `permanentSites`, pelo nome da constante; recopie o arquivo para o Android). Valem com proteções permanentes OU bloqueio ativo, SEM depender de bloqueio temporário: extensão (`evaluatePermanentSites`), `UrlPolicy.cs`, `UrlPolicy.kt` e filtro DNS local do PC (`DnsFilterController.BuildDomains`). Ficam DEPOIS dos motivos mais específicos (lista da pessoa, adulto) e antes de proxy. O hosts NÃO leva a lista permanente (só a lista da pessoa, com bloqueio ativo). O DNS do Android (`DnsVpnService`) NÃO foi alterado.
- **YouTube:** só `/shorts` é barrado (regra de caminho em `isYouTubeShorts`/`UrlPolicy`); youtube.com, youtu.be, googlevideo.com e ytimg.com NÃO estão na lista. Os carrosséis de Shorts na página inicial continuam aparecendo (clicar leva à página de bloqueio); esconder exigiria script de conteúdo.
- **Liberação fixa `music.youtube.com`:** `CTC_ALWAYS_ALLOWED_HOSTS`/`allowedByException` (background.js), `UrlPolicy.cs`, `DnsFilterCore.AlwaysAllowedHosts` e `UrlPolicy.kt`. Continua valendo para youtube.com que a PESSOA coloque na própria lista.
- `App/Services/DefaultSites.cs` agora é só uma MIGRAÇÃO (versão 2): remove da lista da pessoa as 5 entradas de YouTube que a versão 1 juntou. Para acrescentar sites permanentes, edite `CTC_PERMANENT_SITES` (e os testes), aumente `version` do manifest e recopie para o Android.

## Detector de imagens: menos falsos positivos (2026-11-14, NÃO compilado)
- Relato: vídeo de xadrez com close do apresentador travou a tela; suspeita de anime/mangá também no Android. A regra antiga somava hentai+porn+sexy e travava com UMA captura >= 0,80; "sexy" reage a rosto em close e tom de pele.
- `NsfwClassifier.Score` agora devolve `ImageScores` (explícito = porn+hentai, sexy separado, saída completa). `model.json`: `explicitIndices:[1,3]`, `suggestiveIndices:[4]` (o formato antigo `nsfwIndices` ainda é lido). `scripts/converter-modelo-onnx.py` grava o formato novo.
- `App/Services/ImageDecision.cs` (puro): imediato (explícito >= 0,96, 1 captura), explícito (>= 0,85, 2 capturas seguidas), sugestivo (explícito+sexy >= 0,93, 3 capturas). Tabela por rigor em `ImageDecision.Limits`; testes em `tests/SharkBlocker.ParityTests/ImageDecisionTests.cs`. `ImageGuard.Threshold` foi removido.
- `ImageGuard`: captura suspeita não minimiza; a varredura de confirmação roda em ~0,7 s. Log por classe (suspeita / CONFIRMADA / abaixo do limite, só números) para calibrar.
- Aba Proteções: botões "Menos rígido / Padrão / Mais rígido" (`ImageSensitivityButton_Click`); diminuir o rigor exige `UninstallFlow.TryAuthorizeRemoval`, aumentar não. `ImageDecision.cs` NÃO entra no `Watchdog.csproj`.
- O app Android ainda usa a pontuação única antiga: o mesmo ajuste precisa ser espelhado lá (`ImageGuard.kt`/`NsfwClassifier.kt`).
- Limite conhecido: biquíni e close de rosto compartilham a classe "sexy" deste modelo; separar de verdade exige o detector por regiões (NudeNet, seção seguinte; opcional, precisa de `nudenet.onnx`).
- 2026-11-14: removidos `App/src/` (Kotlin do Android que reapareceu por engano no projeto do PC) e `diagnostico-dns.txt` (saída antiga já analisada; agora no `.gitignore`).

## Detector de imagens: segunda opinião NudeNet (2026-11-14, NÃO compilado, sem o modelo real)
- Novos: `App/Services/NudeNetDetector.cs` (ONNX, `Assets/nsfw/nudenet.onnx`, OPCIONAL), `NudeNetDecoder.cs` (puro: saída YOLO `[1,22,N]`/`[1,N,22]` -> exposto/corpo), `OnnxRuntimeLoader.cs` (carrega `onnxruntime.dll` uma vez; o `NsfwClassifier` passou a usá-lo). Regra: `ImageDecision.Refine`/`NudeLimitsFor`/`NudeNetGate`. Testes: `tests/SharkBlocker.ParityTests/NudeNetTests.cs`. Modelo: `scripts/obter-modelo-nudenet.py` (copia do pacote pip `nudenet`; o `.onnx` NÃO está no repositório).
- Efeito: só-sugestivo sem nenhuma parte do corpo é vetado (close de rosto); parte exposta confirma e sobe o nível; Explícito nunca é vetado (anime). Sem o arquivo nada muda. `ImageGuard` chama o NudeNet só com pontuação total do classificador >= 0,30.
- Nenhum destes arquivos entra no `Watchdog.csproj`. Detalhes, licença (MIT no pacote, mas modelo treinado com YOLOv8/AGPL) e o que não foi verificado: `docs/DETECTOR-DE-IMAGENS-PC.md`.

## Diário de recaídas e interface (2026-11-14, NÃO compilado)
- Novos (só App; NENHUM entra no `Watchdog.csproj`): `App/Services/RelapseJournal.cs` (dados em `ProgramData\SharkBlocker\diario.json`, escrita atômica com .bak), `App/Dialogs/JournalEntryWindow.xaml(.cs)` (formulário: gatilho, o que fazia, vontade 1–5, o que teria ajudado), `JournalWindow.xaml(.cs)` (lista, apagar), `JournalUi.cs` (abrir/convite). Testes: `tests/SharkBlocker.ParityTests/JournalTests.cs`.
- Regra: entrada "recaída" chama `StatsStore.RecordRelapse()` (zera a sequência, suja o dia); "só anotou a vontade" não mexe. Apagar a anotação NÃO desfaz a recaída. Sem senha (decisão do usuário). Convite "quer anotar?" 1 vez por abertura do app, só quando a trava por busca/imagem acabou por tempo; a tela de trava em si não abre janelas.
- Privacidade (limite): arquivo sem criptografia e legível por outras contas do Windows (pasta: Users só leitura). Nada vai ao log, à extensão ou ao Android.
- Interface: aba Status com blocos (sequência/recorde/dias limpos/recaídas 30 d) e cartão do diário; pílula de sequência no cabeçalho; ícones nas abas; "Proteções opcionais" em seção recolhível; texto da trava (busca/imagem) cita o diário.
- Não verificado: nada foi compilado nem visto na tela (WPF). Rodar `dotnet build` e `dotnet test` e conferir o visual.

## hosts: negação de escrita que o icacls não removia (2026-11-15, NÃO compilado)
- Sintoma (app.log do usuário): `UnauthorizedAccessException ... drivers\etc\hosts` a cada 5 s no Watchdog (2.528 vezes em 16 h), inclusive em `ApplyBlockedSites` durante um bloqueio. O Watchdog era SYSTEM (confirmado), mas o token do SYSTEM inclui Administradores, então a negação `Write` explícita de Administradores/Usuários também o barrava. `icacls /remove:d` saía com código 0 e a negação continuava (não explicado).
- Correção: `HostsFileManager.RemoveDenyRulesViaAcl()` (API de ACL do .NET) roda em `UnprotectInternal` quando ainda há negação depois do icacls. Loga "removi N regra(s) pela API de ACL". Ainda não verificado no PC do usuário.
- Continuação (log das 10:09): com a correção do ACL instalada o erro seguiu, sem "removi", sem erro de icacls e sem erro de Unprotect. Então ou não há negação e a causa é outra, ou a remoção falha em silêncio. Adicionado `LogHostsStateThrottled()`: no "acesso negado" grava (no máx. 1x a cada 10 min) conta, atributos, dono e a ACL inteira do hosts. Próximo passo: ler essa linha no próximo `app.log`.
- CAUSA do erro do hosts (log das 10:13, linha `hosts (acesso negado)`): a ACL tinha NEGA Write **e Synchronize** para Administradores/Usuários (o `(W)` do icacls inclui SYNCHRONIZE) e o token do SYSTEM inclui Administradores. Sem SYNCHRONIZE nem a LEITURA abria, e `ApplySafeSearchEntries`/`ApplyBlockedSites` liam o hosts ANTES de desproteger (e `ApplyBlockedSites` relia depois de proteger). Correção: a negação passou a ser só `(WD,AD,WEA,WA)` (`DenyWriteRights`) e `ReadLinesTolerant()` desprotege/relê/reprotege se a leitura for negada (proteção antiga). Não verificado no PC.
- Detector: `NudeLimitsFor` subiu (Corroborate 0,65/0,55/0,45; NudeOnly 0,70/0,60/0,50) após falso positivo em vídeo de cachorro (porn 0,90 + NudeNet "seio exposto" 0,38 virava Imediato no Mais rígido). Teste `CachorroNaoViraImediato...`.
