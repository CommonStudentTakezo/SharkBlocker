# Arquitetura de segurança e controle do SharkBlocker

## 1. Visão geral: autorização + serviço nativo + processos cooperativos

```
                 Windows (SCM, Sessão 0)                      Sessão do usuário
        ┌──────────────────────────────────────┐      ┌──────────────────────────────────┐
        │  SharkBlockerWatchdog  (serviço)      │      │  SharkBlocker.exe  (interface)    │
        │  Watchdog.exe · LocalSystem · Auto    │      │  janela · bandeja · trava de tela │
        │                                       │      │                                   │
        │  • aplica hosts / políticas / apps    │◄────►│  • PasswordGate (senha)           │
        │  • vigia o APP (1 s)                  │ pipe │  • ProcessGuardian ("Guard")      │
        │  • reabre o app e trava a tela        │ nome │    vigia o WATCHDOG (3 s)         │
        │  • reaplica `sc failure` e SafeBoot   │  ado │  • batimento -> app-alive.flag    │
        └───────────────┬──────────────────────┘      └───────────────┬──────────────────┘
                        │                                              │
                        └───────────── ProgramData\SharkBlocker ───────┘
                          state.json (+ .bak, backups\) · Registro HKLM\SOFTWARE\SharkBlocker
                          (SYSTEM e Administradores escrevem; Usuários só leem)
```

**Por que o serviço é o `Watchdog.exe` e não o `SharkBlocker.exe`.** O `SharkBlocker.exe` é uma aplicação
WPF (janela, bandeja, trava de tela). Serviços do Windows rodam na Sessão 0, isolada, sem acesso à área
de trabalho: uma janela nunca apareceria. O desenho nativo correto é um serviço sem tela, vivo desde o boot,
que reabre a interface na sessão do usuário (`CreateProcessAsUser`, que exige SYSTEM). Por isso a conta é
`LocalSystem`: editar o `hosts` e as políticas dos navegadores, proteger a pasta e abrir o app na sessão do
usuário não funcionam com uma conta de menos privilégio. O serviço não escuta na rede (só um named pipe
local e HTTP em `127.0.0.1`).

## 2. Modelo de autorização por senha

Toda ação que **desativa regras, remove proteções, fecha o app ou desinstala** passa por um único ponto:

| Ação | Onde | Exige |
|---|---|---|
| Sair pela bandeja | `RequestExit` → `TryAuthorizeRemoval` | trava de tela + **senha** + confirmações |
| Remover itens / lista importada | `TryAuthorizeRemoval` | idem |
| Desativar proteções permanentes | `TryAuthorizeRemoval` | idem |
| Desinstalar (inclui `--uninstall`) | `TryPerformProtectedUninstall` | idem |
| Trocar a senha | `ChangeOrSetPassword` | senha atual |

Regras do `PasswordGate`:

- **Senha obrigatória (fail-closed):** sem senha definida a ação é **recusada**, nunca liberada. A janela
  pede para definir a senha assim que aparece (mínimo 8 caracteres, sem senhas triviais).
- **Limite de tentativas:** 3 erros = 30 s, 5 = 2 min, 7 = 10 min, 10+ = 30 min. O contador fica no Registro,
  assinado com HMAC: fechar e reabrir o app não zera a espera.
- **Hash:** PBKDF2-SHA256 com 600.000 iterações, sal aleatório, comparação em tempo constante. Hashes antigos
  (100.000) são regravados no padrão novo no primeiro acerto.
- **Integridade:** o hash é espelhado no Registro com assinatura; apagar o campo `PasswordHash` do
  `state.json` não desliga a senha (o espelho assinado prevalece).

## 3. Processos cooperativos (Watchdog e Guard)

| Se alguém… | Quem percebe | O que acontece |
|---|---|---|
| finaliza o **app** | Watchdog (≤ 1 s) | trava a tela 30 min e reabre o app (direto na sessão; Agendador de reserva) |
| finaliza o **Watchdog** | Guard (troca de PID / mutex / pipe) e o SCM | serviço volta em ~1 s; trava a tela |
| finaliza **os dois** | SCM + Watchdog novo; ou o app na próxima abertura | o Watchdog novo detecta (marcador `watchdog-running.flag` + batimento) e trava/reabre; se não voltar, o app pune ao abrir |
| congela o Watchdog (processo vivo, sem responder) | Guard (pipe, 15 s) | trava e reinicia o serviço |
| **instala/atualiza** (quick-install, `protection-pause`, após o portão de senha) | marcador `planned-restart.flag` (10 min) | reabre o app **sem** travar |
| fecha o app **normalmente** (senha + confirmações) | `AppPresence.MarkCleanExit` | sem penalidade |

Saúde: o app mostra um indicador no cabeçalho (checagem a cada 60 s) e o script `health-check.ps1` verifica
serviço, processos, batimento, permissões, estado/backups, senha, tarefas agendadas e instalação interrompida.

Trava de tela do **bloqueio agendado**: `ScreenLock.ShowScheduledBlockIfDue` (app, na abertura e a cada tique) mostra a trava até o fim da
janela agendada, recalculado de `BlockScheduler.ActiveUntilUtc` (nada novo é gravado; reiniciar não zera). Teto próprio de 72 h.

## 4. Scripts

| Script | Para quê |
|---|---|
| `install-service.ps1 -InstallRoot <pasta>` | registra o Watchdog como serviço nativo (auto, LocalSystem, reinício em 1 s), confere e abre o app. Idempotente. |
| `uninstall-service.ps1` | abre `SharkBlocker.exe --uninstall` (senha + trava + confirmações). **Não** remove o serviço por conta própria. |
| `health-check.ps1` | diagnóstico somente leitura (código de saída 0/1/2). |
| `quick-install.ps1` | publica e instala de forma transacional (ver README). |
| `protection-pause.ps1` | pausa/retoma as proteções para atualizar arquivos. Pausar passa pelo portão `pause-gate.ps1`: recusa durante bloqueio/trava e exige a senha. |
| `pause-gate.ps1` | o portão acima (também usado por `quick-install.ps1` e pelo instalador). |

## 4.1 Modo seguro

O Windows não carrega serviços de terceiros no modo seguro. O serviço fica registrado em
`HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\SharkBlockerWatchdog` e em `...\SafeBoot\Network\SharkBlockerWatchdog`
(valor padrão `Service`):

| Momento | Quem | O quê |
|---|---|---|
| Instalar o serviço | `ServiceInstaller.Install` (e `install-service.ps1`) | grava as duas chaves |
| A cada ~60 s | `EnforcementWorker` → `ServiceInstaller.EnsureSafeBootRegistration()` | reafirma (idempotente, só grava se faltar/diferir) |
| Pausar proteções | `protection-pause.ps1`, instalador | **não** mexe nas chaves (só desabilita/para o serviço) |
| Desinstalar (após senha) | `ServiceInstaller.Uninstall` | remove as duas chaves, por último |
| Subir em modo seguro | `EnforcementWorker.StartAsync` | lê `SM_CLEANBOOT`; se ≠ 0, loga e aplica hosts/políticas na hora; WMI não é iniciado; `http.sys`/pipe/watcher falham com tolerância |

## 4.2 VPN e proxy

| Camada | Onde | O quê |
|---|---|---|
| Extensões com permissão `proxy` | `BrowserPolicyManager` (camada permanente do Watchdog) | `ExtensionSettings` mesclado, idempotente; remove só o que o app acrescentou (marcador) |
| Programas de VPN/proxy | `ProcessBlocker` (+ `AppLockerManager`) | lista padrão ampliada; lista **opcional** (`BlockOptionalVpnTools`, desligada) |
| Filtro de DNS local + DoH/DoT (#9) | `LocalDnsFilter` + `DnsFilterController` (Watchdog) | opção `LocalDnsFilterEnabled` (desligada); autoteste, fail-open, backup do DNS; regras `SharkBlocker-DNS-*` |
| Firewall contra VPN/proxy/Tor (#10) | `FirewallBlocker` (Watchdog) | regras `netsh` de saída por executável, só com bloqueio ativo; limpeza no fim, na desinstalação e no emergency-fix |
| VMs, WSL, Windows Sandbox (U8) | `ProcessBlocker` | lista **opcional** (`BlockVirtualization`, desligada); `vmmem` pode ser recusado pelo Windows |
| VPN nativa do Windows | `NativeVpnGuard`, chamado pelo Watchdog (SYSTEM) e pelo app (sessão do usuário) a cada varredura do bloqueio | `rasdial /disconnect`; só durante o bloqueio temporizado |

## 4.2.1 Tradutor, cache, arquivo web, IP literal e DoH por nome (U7)

| Camada | Onde | O quê |
|---|---|---|
| Tradutor/cache de buscador | `proxy-terms.js` (`gatewayServices`) + `background.js`/`matcher.js` + espelho `UrlPolicy.cs` | só **durante o bloqueio**; motivo `gateway` (página de bloqueio, sem trava de tela) |
| Arquivo web | `proxy-terms.js` (`archiveServices`) + `BlockState.BlockWebArchives` (botão na aba Proteções, **desligada**) | igual ao acima, só com a opção ligada; a extensão lê `blockWebArchives` em `/state` |
| IP literal público | `matcher.js` (`isPublicLiteralIp`) + espelho `UrlPolicy.IsPublicLiteralIp` | só **durante o bloqueio**; motivo `ip`; localhost e faixas privadas passam |
| DoH por nome | `proxy-terms.js` (`dohHosts`) → `SafeSearchDomains.GetHostsEntries` | linhas `127.0.0.1 <nome>` no bloco do hosts da camada permanente; só nomes exatos |

## 4.3 Subdomínios no hosts

| Quem | Onde | O quê |
|---|---|---|
| Gera as linhas | `HostsFileManager.BuildBlockLines` | `d`, `www.d` e, para os 500 primeiros sites domínio-raiz, `{prefixo}.d` (11 prefixos padrão) |
| Configura | `BlockState.HostsSubdomainPrefixes` | `null` = padrão; lista própria; `[]` = desliga. Validado por `SanitizeSubdomainPrefixes` (rótulo DNS, ≤ 40) |
| Garante o mesmo bloco | app (`MainWindow`), `HostsWatcher`, `EnforcementWorker` (2 pontos) | todos passam `state.HostsSubdomainPrefixes` a `ApplyBlockedSites` |

Os sites digitados vêm antes dos importados na lista; só os primeiros 500 são expandidos, para não multiplicar listas importadas enormes.

## 4.4 Usuário ou perfil novo

| Camada | Onde | O quê |
|---|---|---|
| Tarefa de logon | `AppStartupManager.BuildTaskXml` | grupo Users (`S-1-5-32-545`), gatilho sem `UserId`, `Parallel`; Watchdog recria a versão antiga (`IsRegisteredForAllUsers`) |
| App por sessão | `EnforcementWorker.EnsureAppInEveryActiveSession` + `UserSessionLauncher.GetActiveUserSessions/LaunchInSession` | cada sessão ativa precisa do seu `SharkBlocker.exe`; espera por sessão (1008: 5 s, 740: 5 min, outros: 30 s × falhas) |
| Negar "Parar" | `ServiceInstaller.EnsureStopProtection` / `ClearStopProtection` | SDDL com `(D;;WPDT;;;BA)` na frente; reafirmado a cada ~60 s; pausa, atualização, desinstalação e `emergency-fix-hosts.bat` voltam ao padrão antes de parar |

Conta padrão: o app exige administrador (erro 740) — só a camada de desktop fica de fora; o Watchdog (SYSTEM) segue protegendo.

## 4.5 Políticas de navegador (visitante, DoH, anônimo)

| O quê | Onde | Quando |
|---|---|---|
| `BrowserGuestModeEnabled = 0` (Chrome, Edge, Brave) | `ApplyPermanentChromiumPolicies` | camada permanente |
| `DnsOverHttpsMode = off`, `BuiltInDnsClientEnabled = 0` (Opera, Vivaldi) | `ApplyPermanentChromiumPolicies` (`DohOnlyPolicyKeys`) | camada permanente |
| Anônimo desativado (6 navegadores) | `ApplyRestrictions` (idempotente) | bloqueio temporizado **ou** (`BlockIncognitoAlways` + proteções permanentes) |

`BrowserPolicyManager.IncognitoBlockedAlways(state)` decide o segundo caso; o Watchdog reafirma a cada ciclo e o app só remove o bloqueio ao fim do
bloqueio temporizado se essa função devolver `false`.

## 4.6 Navegadores por metadados

| O quê | Onde | Regra |
|---|---|---|
| Assinaturas por palavras do recurso de versão | `ProcessBlocker.BrowserSignatures` / `ClassifyBrowser` (função pura) | Firefox & derivados, Opera, Vivaldi, Yandex, Iron, Arc, Chromium...: bloqueados em qualquer pasta |
| Chrome, Edge, Brave | `AllowedWhenInstalled` + `IsStandardInstallPath` | bloqueados só fora de Program Files e das pastas por usuário do instalador |
| Cache | `GetBrowserFingerprint` (por caminho, invalida pela data de gravação) | evita reler o disco a cada varredura |
| Motivo no log | `IsBlocked(..., out reason)` | nome, trecho de caminho ou metadados |

## 5. Limites (honestos)

- Tudo isto é **atrito** contra o impulso e contra edição casual, não uma barreira contra um Administrador
  determinado: quem controla o PC pode usar `sc.exe`, `icacls` ou `regedit`. O modo seguro está coberto
  (seção 4.1), mas acesso físico, BIOS destravada, outro sistema de boot, ambiente de recuperação ou edição
  offline do Registro continuam passando; e quem apaga as chaves SafeBoot e reinicia em modo seguro antes do
  próximo ciclo (~60 s) também.
- A chave de assinatura (HMAC) está embutida no binário: é ofuscação, não criptografia.
- Esqueceu a senha? Não há recuperação pelo app (de propósito). O caminho é o de Administrador:
  remoção manual (`sc.exe`, `icacls`, `regedit`), o que o próprio modelo trata como adulteração deliberada. O `protection-pause.ps1` NÃO serve mais de atalho: ele exige a senha e recusa durante bloqueio.

## Limites fora do alcance do app (U8)

WSL2, máquinas virtuais, live USB, outros aparelhos e outros computadores não passam pelo `hosts`, pelas políticas nem pelo Watchdog do Windows. Só
mitigações externas ajudam (DNS de filtragem no roteador, senha de BIOS/UEFI com boot por USB desligado, BitLocker, WSL/Hyper-V desativados). Lista e comandos no README.
