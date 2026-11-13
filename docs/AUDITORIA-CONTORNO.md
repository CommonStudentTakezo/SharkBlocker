# SharkBlocker: auditoria de contorno

**Data:** 23/10/2026
**Escopo:** o que o app já cobre e o que escapa em quatro vetores (outro navegador, modo seguro do Windows, DNS alternativo, perfil de usuário novo), mais os caminhos extras que apareceram na leitura.
**Método:** revisão estática do código do `SharkBlocker-completo.zip`. **Nada foi executado.** Os itens marcados "a confirmar" só um teste real no Windows resolve.

---

## 1. Resumo

O núcleo é bom: `hosts` protegido por ACL, políticas de navegador em HKLM, serviço SYSTEM com reinício automático, tarefa de reavivamento a cada minuto e senha com falha fechada no instalador e na pausa.

A defesa depende quase toda do **Watchdog estar rodando**. O maior risco é qualquer situação em que ele não sobe ou é parado. O modo seguro é o caso mais claro.

| # | Urgência | Achado |
|---|---|---|
| U1 | **Crítico** | Serviço não é registrado para o modo seguro |
| U2 | **Alto** | VPN e proxy: lista curta, extensões da Web Store liberadas, VPN nativa do Windows |
| U3 | **Alto** | `hosts` só bloqueia `dominio` e `www.dominio` (subdomínios passam) |
| U4 | Médio | Usuário/perfil novo: tarefa de logon presa ao instalador e janela sem vigia |
| U5 | Médio | Políticas de navegador incompletas (visitante, DoH em Opera/Vivaldi, anônimo fora do bloqueio) |
| U6 | Médio | Detecção de navegador por nome do processo (renomear contorna) |
| U7 | Baixo | Proxies web, tradutor, cache, IP literal e DoH por nome |
| U8 | Baixo | Fora do alcance do app: WSL, máquina virtual, live USB, outros dispositivos |

---

## 2. Achados, em ordem de urgência

### U1. Modo seguro (CRÍTICO)

**Problema.** Não existe nenhuma referência a `SafeBoot`, `bcdedit` ou modo seguro no projeto. No modo seguro o Windows não carrega serviços de terceiros nem tarefas agendadas. O Watchdog (`SharkBlockerWatchdog`) não sobe, então ninguém reverte edições no `hosts`, ninguém mata processos e ninguém detecta adulteração. A senha protege a interface do app, não a máquina.

**Evidência.** Busca por `SafeBoot|bcdedit|Minimal|modo seguro|safe mode` sem resultado. `App/Services/ServiceInstaller.cs` cria o serviço com `start= auto` e configura só `sc failure`.

**Correção.**
1. No `ServiceInstaller.Install`, gravar o valor padrão `Service` em `HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\SharkBlockerWatchdog` e em `HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\SharkBlockerWatchdog`. É o mecanismo documentado que antivírus usam.
2. Remover essas chaves no desinstalar do serviço.
3. Reafirmar as chaves a cada ciclo do Watchdog, no mesmo padrão de `EnsureFailureActionsConfigured` (barato e idempotente), para que apagá-las não desligue a proteção em silêncio.
4. No início do Watchdog, ler `GetSystemMetrics(SM_CLEANBOOT)` (índice 67). Se for diferente de 0 (1 = seguro, 2 = seguro com rede), registrar no log e aplicar `hosts` e políticas imediatamente, sem esperar o primeiro ciclo.
5. Garantir que componentes que dependem de serviços ausentes no modo seguro (por exemplo WMI no `ProcessStartWatcher`) falhem de forma tolerante e não derrubem o Watchdog.

**Teste.** Reiniciar em Modo Seguro com Rede, confirmar `SharkBlockerWatchdog` em execução (`sc query`), tentar editar o `hosts` e ver a edição revertida, abrir um navegador na lista e ver o processo encerrado durante o bloqueio.

**Limite.** Quem tem acesso físico, BIOS destravada ou outro sistema de boot ainda passa (ver U8).

---

### U2. VPN e proxy (ALTO)

**Problema.**
- `ProcessBlocker` e `AppLockerManager` cobrem só VPNs conhecidas por nome. Faltam, por exemplo, Cloudflare WARP (`warp-svc`), Psiphon, Lantern, TunnelBear e UltraSurf.
- A lista de bloqueio de extensões foi removida (corretamente, porque o `"*"` antigo derrubava todas as extensões). Resultado: extensões de VPN e proxy da Web Store instalam normalmente.
- A VPN nativa do Windows (Configurações > VPN) não tem processo de terceiros para matar.
- Durante o bloqueio, o bloqueio por processo só roda no ciclo temporizado.

**Evidência.** `App/Services/ProcessBlocker.cs` (`BlockedProcessNames`), `App/Services/AppLockerManager.cs`, `App/Services/BrowserPolicyManager.cs` (`CleanUpLegacyArtifacts` só desfaz o legado).

**Correção.**
1. **Extensões, sem depender de IDs:** gravar a política `ExtensionSettings` (REG_SZ com JSON) nos Chromium com `"*": { "blocked_permissions": ["proxy"] }`, mesclando com qualquer `ExtensionSettings` já existente e nunca sobrescrevendo-o. Confirmar o efeito em `chrome://policy` e `edge://policy`. Alternativa ou complemento: a extensão do app usar a API `chrome.management` (permissão `management`) para desativar outras extensões que peçam `proxy`.
2. **Não inventar IDs de extensão.** Se for usar lista de IDs, ela deve ser configurável pelo usuário, validada pelo formato (32 letras de `a` a `p`) e começar vazia ou só com IDs conferidos na loja.
3. **Processos:** ampliar `BlockedProcessNames` e `SuspiciousPathKeywords` com as VPNs acima. Itens de uso legítimo comum (por exemplo Tailscale) entram como opção configurável, desligada por padrão.
4. **VPN nativa:** durante o bloqueio, detectar conexões VPN/RAS ativas e interfaces de túnel e desconectar (`rasdial /disconnect` ou equivalente), registrando no log.

**Teste.** Instalar uma extensão de proxy de teste (ou criar uma com permissão `proxy`) e confirmar que ela é bloqueada ou desativada. Subir uma VPN nativa e ver a desconexão durante o bloqueio.

---

### U3. Subdomínios no `hosts` (ALTO) — implementado (ver README, "Subdomínios no hosts")

**Problema.** O `hosts` não aceita curinga. O app grava só `127.0.0.1 dominio.com` e `127.0.0.1 www.dominio.com`. Subdomínios como `m.`, `mobile.` ou `pt.` resolvem normalmente. Onde a extensão não roda (Firefox, apps com navegador embutido, anônimo fora do bloqueio) esse é o caminho aberto.

**Evidência.** `App/Services/HostsFileManager.cs`, trecho que monta `127.0.0.1 {d}` e `127.0.0.1 www.{d}`.

**Correção.**
1. **Imediata e de baixo risco:** gerar também uma lista configurável de prefixos comuns (`m`, `mobile`, `amp` e os de idioma) para cada domínio. Cuidar do tamanho do arquivo e do desempenho do Windows com `hosts` grande.
2. **A avaliar (precisa de teste):** a política de resolução de nomes por sufixo do cliente DNS do Windows (NRPT, em `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DnsPolicyConfig`) trata todos os subdomínios de um sufixo. Verificar o comportamento com servidor inexistente (tempo de espera) antes de adotar.
3. Documentar no README que o `hosts` é por nome exato e que a extensão cobre subdomínios nos navegadores onde roda.

**Teste.** Com um domínio de teste bloqueado, abrir `m.dominio` no Firefox e em um app Electron e confirmar o bloqueio.

---

### U4. Usuário ou perfil novo (MÉDIO) — implementado (ver README, "Usuário ou perfil novo")

**O que já funciona.** Políticas em HKLM valem para todos os usuários e perfis. O Watchdog (SYSTEM) enxerga processos de todas as sessões e relança o app na sessão ativa (`UserSessionLauncher` com `WTSGetActiveConsoleSessionId`). A extensão à força vale para todos os perfis do navegador.

**Problema.**
- `App/Services/AppStartupManager.cs` cria a tarefa de logon para `WindowsIdentity.GetCurrent().Name`, ou seja, só o usuário que instalou. Em um usuário novo o app só aparece pelo relançamento do Watchdog, com intervalo de 20 s. Nesse tempo não há vigia de barra de endereço nem trava de tela (a confirmar).
- O relançamento usa só a sessão de console ativa, então troca rápida de usuário pode deixar sessões sem app.
- Um administrador novo pode parar o serviço com parada limpa (o SCM não reinicia). A tarefa de reavivamento de 1 min compensa, mas também pode ser desativada por ele.

**Correção.**
1. Criar a tarefa de logon com o grupo Users (`S-1-5-32-545`) como principal e gatilho de logon sem `UserId`, para valer para qualquer conta.
2. Reduzir o intervalo de relançamento quando o app não está rodando e há sessão ativa (por exemplo 5 s).
3. Enumerar sessões com `WTSEnumerateSessions` e garantir o app em cada sessão ativa, não só na do console.
4. Opcional: ajustar o descritor de segurança do serviço (`sc sdset`) para negar parada a usuários interativos, reafirmando a cada ciclo. Limite: administrador com tempo e vontade ainda consegue mudar isso.

**Teste.** Criar uma conta padrão e uma administradora, entrar, e medir em quanto tempo o app e o vigia aparecem. Tentar `sc stop SharkBlockerWatchdog` e ver o reavivamento.

---

### U5. Políticas de navegador incompletas (MÉDIO) — implementado (ver README, "Políticas de navegador")

**Problema.**
- Não há política para **modo visitante** (`BrowserGuestModeEnabled`) em Chrome, Edge e Brave. Perfil visitante não carrega extensões.
- `DnsOverHttpsMode` e `BuiltInDnsClientEnabled` só são gravados para Chrome, Edge e Brave. Opera e Vivaldi ficam de fora (os caminhos deles já existem em `IncognitoTargets`).
- O bloqueio de modo anônimo só vale durante o bloqueio temporizado (`ApplyRestrictions` / `RemoveRestrictions`). Fora dele o anônimo fica liberado e a extensão, em geral, não roda lá (a confirmar). O `hosts` e o vigia de barra de endereço continuam valendo.

**Correção.**
1. Gravar `BrowserGuestModeEnabled = 0` nos três Chromium.
2. Replicar as políticas de DoH para Opera e Vivaldi e validar em `opera://policy` e `vivaldi://policy`.
3. Decisão sua: aplicar o bloqueio de anônimo também quando `PermanentProtectionsEnabled` estiver ligado, com um interruptor de configuração.

**Teste.** Abrir visitante e anônimo em cada navegador e comparar com `chrome://policy`.

---

### U6. Detecção de navegador por nome do processo (MÉDIO) — implementado (ver README, "Navegadores renomeados")

**Problema.** `ProcessBlocker` compara o nome do executável. Um navegador portátil renomeado, ou um fork Chromium fora da lista (Yandex, Arc, Iron e outros), não é reconhecido. `chromium` aparece no vigia de barra mas não na lista de processos mortos.

**Correção.** Além do nome, identificar por metadados do arquivo (`FileVersionInfo.OriginalFilename`, `ProductName`) e tratar como bloqueado, durante o bloqueio, qualquer executável de navegador conhecido que rode fora dos diretórios de instalação padrão. Manter `NeverKill` e as exceções já existentes.

**Teste.** Copiar um navegador portátil, renomear o `.exe` e confirmar que é encerrado durante o bloqueio.

---

### U7. Proxies web, tradutor, cache, IP literal e DoH por nome (BAIXO) — implementado (ver README, "Tradutor, cache, arquivo web, IP literal e DoH por nome")

**Problema.**
- `BrowserExtension/proxy-terms.js` não cobre acesso via tradutor, cache e serviços de arquivo web.
- Acessar o site por **IP literal** ignora o `hosts`.
- Provedores de DoH por nome não estão bloqueados.

**Correção.**
1. Acrescentar ao `proxy-terms.js` padrões de tradutor e de cache. Serviços de arquivo web têm uso legítimo grande, então entram como opção desligada por padrão.
2. No `matcher.js`, durante o bloqueio, bloquear navegação por IP literal (IPv4 e IPv6), exceto localhost e faixas privadas.
3. Avaliar bloquear no `hosts` os nomes de provedores de DoH conhecidos (os navegadores caem para o DNS do sistema).
4. Atualizar `tests/extension/run-tests.js` com casos novos.

---

### U8. Fora do alcance do app (BAIXO, só mitigação) — documentado (ver README, "Fora do alcance do app"; sem código)

WSL2 e máquinas virtuais têm rede e `hosts` próprios. Live USB, outro dispositivo e outro computador nem passam pelo Windows. O app não resolve isso sozinho.

**Parcialmente coberto (2026-11-01):** a opção `BlockVirtualization` (aba Proteções, desligada por padrão) encerra WSL, Windows Sandbox, VirtualBox, VMware e QEMU durante o bloqueio. Não cobre live USB, outro dispositivo, outro computador nem uma VM que o Windows se recuse a encerrar (`vmmem`).

**Mitigações externas:** DNS de filtragem no roteador (por exemplo OpenDNS FamilyShield, CleanBrowsing ou NextDNS); senha de BIOS/UEFI e boot por USB desligado; BitLocker; desativar WSL e Hyper-V se não usar.

---

## 3. O que já está bem coberto

- **DNS alternativo:** trocar para 8.8.8.8 ou 1.1.1.1 não libera os domínios listados, porque o `hosts` é consultado antes. DoH desligado por política em Chrome, Edge, Brave e Firefox (travado).
- **`hosts`:** somente leitura + atributo System + negação de escrita para Administradores e Usuários, com `HostsWatcher` reaplicando.
- **Pasta de instalação:** protegida por ACL contra remoção.
- **Serviço:** `sc failure` reaplicado a cada ciclo, tarefa de reavivamento a cada minuto, detecção de encerramento forçado do app com trava de 30 min.
- **Atualizar e desinstalar:** exigem senha, com falha fechada.
- **Navegadores:** extensão à força no Chrome, Edge e Brave; vigia de barra no Chrome, Edge, Brave, Opera, Vivaldi, Firefox, LibreWolf e Waterfox.

## 4. Limites aceitos (já no README)

Ctrl+Alt+Del não pode ser bloqueado; o botão de desligar sempre funciona (válvula de segurança); um administrador que assume a propriedade dos arquivos acaba passando.

## 5. Plano de testes rápido

1. **U1:** boot em Modo Seguro com Rede, serviço ativo, `hosts` revertido.
2. **U2:** extensão com permissão `proxy` e VPN nativa durante o bloqueio.
3. **U3:** `m.dominio` no Firefox e em app Electron.
4. **U4:** conta padrão e conta administradora novas; `sc stop` no serviço.
5. **U5:** visitante e anônimo em cada navegador.
6. **U6:** navegador portátil renomeado.
7. **U7:** URL por IP literal e por tradutor.

Em todos os casos, conferir também que **pausa, atualização e desinstalação** continuam funcionando com senha.

---

## 6. Prompt de implementação

Copie o bloco abaixo para o agente de código (Claude Code ou similar), na raiz do projeto `SharkBlocker`.

````
Você vai implementar correções de segurança no projeto SharkBlocker (bloqueador de conteúdo de uso pessoal para Windows: App WPF .NET 8, Watchdog como serviço do Windows, BrowserExtension MV3). Leia primeiro AGENTS.md, ARCHITECTURE.md e docs/AUDITORIA-CONTORNO.md (ou o arquivo AUDITORIA-CONTORNO.md na raiz), que descreve cada achado com evidência.

REGRAS GERAIS
- Idioma da interface, logs e comentários: português do Brasil. Mantenha o estilo dos comentários existentes.
- O projeto usa WPF e WinForms juntos e o SDK adiciona "using System.Drawing" e "using System.Windows.Forms" globalmente. Qualifique tipos ambíguos (Brush, Color, FontFamily, Image, Orientation, HorizontalAlignment etc.) ou use alias no topo do arquivo, como já é feito.
- Toda gravação de Registro deve ser idempotente (padrão SetIfDifferent): o Watchdog roda a cada 5 s e não deve regravar valores iguais.
- Falhar fechado: se algo não puder ser verificado, trate como negativo, como já fazem PasswordGate e o instalador. Nunca enfraqueça a senha, a pausa (protection-pause.ps1, pause-gate.ps1), a atualização (Installer.iss) nem UninstallFlow.
- Não invente IDs de extensão do Chrome. Qualquer lista de IDs deve ser configurável, validada por regex ^[a-p]{32}$ e começar vazia.
- Não derrube o PC nem programas legítimos: respeite a lista NeverKill e não mate processos por trechos genéricos de caminho.
- Mantenha CleanUpLegacyArtifacts intacto (só apaga exatamente o que a versão antiga gravava).
- Comentários que descrevem limites devem ser honestos, no estilo "LIMITE HONESTO" já usado.

TAREFAS, NESTA ORDEM (uma por vez; compile e rode os testes antes de seguir)

U1 (crítico) Modo seguro
- ServiceInstaller: ao instalar o serviço "SharkBlockerWatchdog", gravar o valor padrão "Service" em HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\SharkBlockerWatchdog e em ...\SafeBoot\Network\SharkBlockerWatchdog. Remover as duas chaves ao desinstalar o serviço. Criar EnsureSafeBootRegistration() idempotente e chamá-lo no mesmo ponto em que EnsureFailureActionsConfigured é chamado a cada ciclo.
- Watchdog/EnforcementWorker: no início, ler GetSystemMetrics(SM_CLEANBOOT = 67). Se != 0, registrar no log e aplicar hosts e políticas imediatamente. Garantir que ProcessStartWatcher/WMI e qualquer dependência indisponível no modo seguro falhem com tolerância (log, sem derrubar o serviço).
- Verificar que Installer.iss, quick-install.ps1, install-service.ps1 e protection-pause.ps1 continuam coerentes (a pausa não deve apagar as chaves SafeBoot; a desinstalação deve).

U2 (alto) VPN e proxy
- BrowserPolicyManager: gravar a política ExtensionSettings (REG_SZ JSON) nos Chromium (Chrome, Edge, Brave) com "*": {"blocked_permissions": ["proxy"]}, MESCLANDO com um ExtensionSettings já existente (nunca sobrescrever). Remover somente a parte nossa ao remover proteções. Se houver dúvida sobre o efeito, deixe um comentário pedindo validação em chrome://policy.
- ProcessBlocker/AppLockerManager: acrescentar Cloudflare WARP (warp-svc, "Cloudflare WARP"), Psiphon, Lantern, TunnelBear, UltraSurf. Itens de uso legítimo comum (ex.: Tailscale) ficam numa lista opcional desligada por padrão.
- Detectar e desconectar VPN nativa do Windows (conexões RAS/VPN ativas, interfaces de túnel) durante o bloqueio temporizado, com log.

U3 (alto) Subdomínios no hosts
- HostsFileManager: além de "d" e "www.d", gerar entradas para uma lista configurável de prefixos comuns (m, mobile, amp, e idiomas), com limite de tamanho do arquivo e sem duplicatas. Manter a normalização de domínio e o fluxo de Protect/Unprotect, o Mutex e o HostsWatcher.
- Documente no README o limite (hosts é por nome exato). Não implemente NRPT sem teste; deixe como nota.

U4 (médio) Usuário novo
- AppStartupManager: tarefa de logon com o grupo Users (S-1-5-32-545) como principal e gatilho sem UserId. Manter RL HIGHEST e --minimized, e a remoção das tarefas legadas.
- EnforcementWorker/UserSessionLauncher: reduzir o intervalo de relançamento do app (ex.: 5 s quando não está rodando e há sessão ativa) e relançar em cada sessão ativa via WTSEnumerateSessions, não só na do console.
- Opcional: sc sdset negando SERVICE_STOP a usuários interativos, reafirmado a cada ciclo, com comentário de limite honesto.

U5 (médio) Políticas de navegador
- BrowserGuestModeEnabled=0 em Chrome, Edge e Brave.
- DnsOverHttpsMode="off" e BuiltInDnsClientEnabled=0 também em Opera e Vivaldi (caminhos já presentes em IncognitoTargets).
- Adicionar a opção de aplicar o bloqueio de modo anônimo também quando PermanentProtectionsEnabled estiver ligado (padrão: manter o comportamento atual).

U6 (médio) Navegadores renomeados
- ProcessBlocker: além do nome, usar FileVersionInfo (OriginalFilename, ProductName) para identificar navegadores conhecidos; durante o bloqueio, encerrar os que rodem fora dos diretórios padrão de instalação. Preservar NeverKill e cuidar de exceções de acesso.

U7 (baixo) Extensão
- proxy-terms.js: padrões de tradutor e de cache; serviços de arquivo web atrás de opção desligada por padrão.
- matcher.js: durante o bloqueio, bloquear navegação por IP literal (IPv4/IPv6), exceto localhost e faixas privadas.
- Atualizar tests/extension/run-tests.js com casos positivos e negativos para cada mudança.

ENTREGA
- Ao final de cada tarefa: rode `dotnet build` (App e Watchdog, win-x64) e `node tests/extension/run-tests.js`; corrija erros antes de seguir.
- Atualize README.md e ARCHITECTURE.md com o que mudou e os limites.
- Entregue um resumo por tarefa: arquivos alterados, o que foi testado e o que NÃO pôde ser testado (por exemplo Modo Seguro, usuário novo e VPN nativa exigem Windows real; liste os testes manuais da seção 5 do documento de auditoria).
- Não faça refatorações fora do escopo. Se uma tarefa conflitar com o código existente, pare e explique em vez de adivinhar.
````
