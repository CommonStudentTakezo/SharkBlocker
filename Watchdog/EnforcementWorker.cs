using SharkBlocker.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace SharkBlocker.Watchdog;

/// <summary>
/// Serviço em segundo plano com duas camadas independentes:
///  1) PERMANENTE (roda sempre, mesmo sem bloqueio ativo, enquanto
///     PermanentProtectionsEnabled=true): SafeSearch, DoH desligado, extensão
///     instalada, pasta protegida.
///  2) TEMPORIZADA (só enquanto IsLocked=true): sites/apps/palavras-chave da
///     lista, modo anônimo desativado.
/// Isso resolve o "SafeSearch não fica sempre ativo" — antes ele só era
/// aplicado durante o bloqueio e removido no final.
/// </summary>
public class EnforcementWorker : BackgroundService
{
    private readonly ILogger<EnforcementWorker> _logger;
    private HostsWatcher? _hostsWatcher;
    private LocalStateServer? _stateServer;
    private ProcessStartWatcher? _processWatcher;
    private bool _lockRestrictionsApplied;
    private DateTime _lastAppLaunchRequest = DateTime.MinValue;
    private int _cycleCount;
    private bool _folderProtectedOnce;
    private readonly CancellationTokenSource _monitorCts = new();
    private string InstallRootPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    private string ExtensionFolderPath => Path.Combine(InstallRootPath, "BrowserExtension");

    public EnforcementWorker(ILogger<EnforcementWorker> logger)
    {
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.Log("Watchdog iniciado (StartAsync).");

        // MODO SEGURO: o serviço só chega aqui no modo seguro por causa das chaves SafeBoot que o
        // ServiceInstaller registra. Nesse modo o Windows sobe só o mínimo (WMI, http.sys e outros
        // serviços podem não existir), então registramos o fato e aplicamos o bloqueio JÁ, antes de
        // qualquer inicialização lenta ou que possa falhar abaixo.
        var cleanBoot = ServiceInstaller.GetCleanBootMode();
        if (cleanBoot != 0)
        {
            var nome = cleanBoot == 2 ? "MODO SEGURO COM REDE" : "MODO SEGURO";
            Logger.Log($"{nome} detectado (SM_CLEANBOOT={cleanBoot}) — aplicando hosts e políticas imediatamente; " +
                       "componentes indisponíveis neste modo (WMI, servidor local) falham com tolerância.");
            EnforceImmediately();
        }

        // PRIMEIRA COISA, antes de qualquer inicialização lenta (WMI, servidor local...): se a
        // instância anterior deste serviço foi morta à força, trava a tela AGORA, e começa já a
        // vigiar/reabrir o app. (Quem foi morto não pode agir; quem o substitui age ao nascer.)
        var previousWatchdogKilled = AppPresence.MarkWatchdogStarted();
        if (previousWatchdogKilled && AppPresence.IsPlannedRestart(TimeSpan.FromMinutes(10)))
        {
            Logger.Log("O Watchdog anterior foi encerrado por um reinício PLANEJADO (instalação/atualização) — sem trava.");
            previousWatchdogKilled = false;
        }
        if (previousWatchdogKilled)
        {
            Logger.Log("O WATCHDOG ANTERIOR FOI ENCERRADO À FORÇA (Gerenciador de Tarefas ou outro meio) — travando a tela por 30 min.");
            try { StateManager.StartScreenLockIfNotActive(StateManager.ScreenLockPenalty, AppPresence.ReasonClosed); }
            catch (Exception ex) { Logger.LogException("Watchdog.StartAsync (trava por Watchdog morto)", ex); }
        }
        _ = Task.Run(() => MonitorAppAsync(_monitorCts.Token, previousWatchdogKilled));

        // Mata qualquer OUTRA instância de Watchdog.exe rodando (exceto esta),
        // caso alguma tenha sobrevivido de uma reinstalação anterior. Isso
        // complementa a limpeza que o App já faz — importante porque este
        // Watchdog pode ter sido reiniciado sozinho pela recuperação automática
        // do Windows, sem o App estar aberto pra fazer essa limpeza.
        var myPid = Environment.ProcessId;
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("Watchdog"))
        {
            if (proc.Id != myPid)
            {
                try
                {
                    Logger.Log($"Encerrando outra instância de Watchdog.exe (PID {proc.Id}) encontrada ao iniciar.");
                    proc.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Logger.LogException("EnforcementWorker.StartAsync (limpeza de outras instâncias)", ex);
                }
            }
            proc.Dispose();
        }

        // Cada componente abaixo é isolado: no modo seguro alguns (http.sys, WMI) podem não existir, e uma
        // exceção aqui derrubaria o serviço inteiro. O ciclo de 5 s do ExecuteAsync é a rede de segurança.
        try { _hostsWatcher = new HostsWatcher(StateManager.Load); }
        catch (Exception ex) { Logger.LogException("Watchdog.StartAsync (HostsWatcher)", ex); _hostsWatcher = null; }

        try
        {
            _stateServer = new LocalStateServer(StateManager.Load);
            _stateServer.Start();
        }
        catch (Exception ex)
        {
            // Sem o servidor local a extensão não lê o estado, mas o hosts e as políticas seguem valendo.
            Logger.LogException("Watchdog.StartAsync (LocalStateServer)", ex);
            try { _stateServer?.Dispose(); } catch { }
            _stateServer = null;
        }

        // ProcessStartWatcher.Start já se protege e não inicia o WMI no modo seguro.
        try
        {
            _processWatcher = new ProcessStartWatcher(StateManager.Load);
            _processWatcher.Start();
        }
        catch (Exception ex) { Logger.LogException("Watchdog.StartAsync (ProcessStartWatcher)", ex); }

        // Sincronização "oficial" com o App (via Mutex) e com o Guard, a vigília que o App mantém
        // sobre este serviço (via Named Pipe) — ver ProcessSync e ProcessGuardian. Isto é ADICIONAL
        // à recuperação automática do SCM (sc failure) já configurada em ServiceInstaller.Install:
        // aquela religa o PROCESSO se ele morrer; isto deixa o App saber que o processo está de pé
        // E respondendo, sem precisar enumerar processos ou rodar "sc query" a cada checagem.
        try
        {
            ProcessSync.HoldWatchdogAlive();
            ProcessSync.StartStatusPipeServer(BuildStatusJson);
        }
        catch (Exception ex) { Logger.LogException("Watchdog.StartAsync (ProcessSync)", ex); }

        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Aplica já o que o primeiro ciclo de <see cref="ExecuteAsync"/> aplicaria (hosts, SafeSearch,
    /// políticas de navegador, encerramento de apps bloqueados e proteção do hosts), sem esperar a
    /// inicialização lenta do serviço. Hoje só é usado ao subir em modo seguro. Tudo é idempotente, então
    /// o primeiro ciclo logo depois não regrava nada igual. Nunca lança: falhas vão para o log.
    /// Não instala a extensão (operação pesada): isso fica com o ciclo normal.
    /// </summary>
    private static void EnforceImmediately()
    {
        try
        {
            var state = StateManager.Load();
            if (BlockScheduler.EnsureActive()) state = StateManager.Load();

            if (state.PermanentProtectionsEnabled)
            {
                HostsFileManager.ApplySafeSearchEntries(SafeSearchDomains.GetHostsEntries());
                BrowserPolicyManager.ApplyPermanentChromiumPolicies();
                if (BrowserPolicyManager.IncognitoBlockedAlways(state)) BrowserPolicyManager.ApplyRestrictions(); // U5
            }

            if (state.IsLocked)
            {
                var domains = (state.BlockedWebsites ?? new List<string>())
                    .Concat(state.ImportedWebsites ?? new List<string>())
                    .ToList();
                HostsFileManager.ApplyBlockedSites(domains, state.HostsSubdomainPrefixes);
                ProcessBlocker.KillBlockedProcesses(state.BlockedProcesses, state.BlockedProcessPathKeywords);
                FirewallBlocker.EnforceWhileLocked(ProcessBlocker.TakeKilledPaths());
            }

            if (state.PermanentProtectionsEnabled || state.IsLocked) HostsFileManager.Protect();

            Logger.Log($"Aplicação imediata concluída (Locked={state.IsLocked}, PermanentProtections={state.PermanentProtectionsEnabled}).");
        }
        catch (Exception ex)
        {
            Logger.LogException("Watchdog.EnforceImmediately", ex);
        }
    }

    /// <summary>Resumo do estado atual, servido pelo pipe de status a quem perguntar (hoje: só o Guard, dentro do App).</summary>
    private string BuildStatusJson()
    {
        var state = StateManager.Load();
        var payload = new
        {
            pid = Environment.ProcessId,
            cycles = _cycleCount,
            utcNow = DateTime.UtcNow,
            isLocked = state.IsLocked,
            screenLockActive = state.IsScreenLockActive,
            lockedUntilUtc = state.LockedUntilUtc,
            permanentProtectionsEnabled = state.PermanentProtectionsEnabled
        };
        return JsonSerializer.Serialize(payload);
    }

    // OTIMIZAÇÃO: esta checagem roda a cada segundo. Listar todos os processos do sistema (o que
    // GetProcessesByName faz) toda vez é caro; segurando o handle do app, o caso normal ("ainda vivo")
    // vira uma simples consulta de handle. Se o app morrer, reprocura (pode haver outra instância).
    private static readonly object AppProcGate = new();
    private static System.Diagnostics.Process? _appProc;

    private static bool IsAppRunning()
    {
        lock (AppProcGate)
        {
            try
            {
                if (_appProc != null)
                {
                    if (!_appProc.HasExited) return true;
                    _appProc.Dispose();
                    _appProc = null;
                }
            }
            catch
            {
                try { _appProc?.Dispose(); } catch { }
                _appProc = null;
            }

            var procs = System.Diagnostics.Process.GetProcessesByName("SharkBlocker");
            if (procs.Length == 0) return false;

            _appProc = procs[0];
            for (var i = 1; i < procs.Length; i++) procs[i].Dispose();
            return true;
        }
    }

    /// <summary>
    /// VIGIA O APP a cada segundo. Se o processo some sem a marca de "saída normal"
    /// (<see cref="AppPresence"/>), foi encerrado à força — pelo Gerenciador de Tarefas, taskkill
    /// ou qualquer outro meio: trava a tela por 30 min e reabre o app (que desenha a trava).
    ///
    /// CORRIGIDO: antes a reabertura era tentada UMA única vez (e o "pendente" era zerado antes de
    /// saber se deu certo). Se o Agendador de Tarefas falhasse, o app nunca mais voltava. Agora a
    /// reabertura fica PENDENTE e é repetida até o processo reaparecer; a partir da 2ª tentativa
    /// (ou se a tarefa agendada não existir) o app é aberto direto na sessão do usuário.
    /// </summary>
    private async Task MonitorAppAsync(CancellationToken ct, bool watchdogWasKilled)
    {
        // CORRIGIDO: antes só valia se o último batimento fosse de < 30 s. Quando app e Watchdog eram
        // finalizados juntos, qualquer demora na volta do serviço (ele sobe WMI, servidor local...
        // antes do monitor) deixava o fechamento forçado passar sem trava. Agora vale "vivo desde o
        // boot do Windows, até 10 min atrás, sem saída normal registrada".
        // watchdogWasKilled: o Watchdog anterior morreu (provavelmente junto com o app): se o app
        // não está rodando, é fechamento forçado mesmo que o batimento esteja velho.
        var wasRunning = AppPresence.WasAliveSinceBoot(TimeSpan.FromMinutes(10)) || watchdogWasKilled;
        // Diagnóstico: se um fechamento forçado passar sem trava, estas três informações dizem por quê.
        Logger.Log($"MonitorApp: Watchdog PID {Environment.ProcessId} começou a vigiar o app. " +
                   $"app rodando={IsAppRunning()}, batimento do app recente (<=10 min, desde o boot)={wasRunning}, " +
                   $"saída normal recente={AppPresence.WasCleanExitRecent(TimeSpan.FromSeconds(45))}.");
        if (wasRunning && !IsAppRunning())
            Logger.Log("Watchdog iniciou e o app estava vivo antes, sem saída normal registrada — tratando como encerramento forçado.");
        var pendingRelaunch = false;
        var lastAttemptUtc = DateTime.MinValue;
        var attempts = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (IsAppRunning())
                {
                    wasRunning = true;
                    if (pendingRelaunch)
                    {
                        Logger.Log($"App reaberto com sucesso após {attempts} tentativa(s).");
                        pendingRelaunch = false;
                        attempts = 0;
                    }
                }
                else
                {
                    if (wasRunning)
                    {
                        wasRunning = false;
                        if (AppPresence.WasCleanExitRecent(TimeSpan.FromSeconds(45)))
                        {
                            Logger.Log("O app fechou de forma normal (saída autorizada, fim de sessão ou falha interna) — sem penalidade.");
                            AppPresence.Clear();
                        }
                        else if (AppPresence.IsPlannedRestart(TimeSpan.FromMinutes(10)))
                        {
                            // Instalação/atualização encerrou o app de propósito: reabre, mas NÃO trava.
                            Logger.Log("O app foi encerrado por um reinício PLANEJADO (instalação/atualização) — reabrindo sem travar a tela.");
                            AppPresence.ClearAlive();
                            pendingRelaunch = true;
                            attempts = 0;
                            lastAttemptUtc = DateTime.MinValue;
                        }
                        else
                        {
                            Logger.Log("O APP FOI ENCERRADO À FORÇA (Gerenciador de Tarefas ou outro meio) — travando a tela por 30 min e reabrindo.");
                            try
                            {
                                StateManager.StartScreenLockIfNotActive(StateManager.ScreenLockPenalty, AppPresence.ReasonClosed);
                            }
                            catch (Exception ex)
                            {
                                // Uma falha ao gravar a trava NÃO pode impedir a reabertura.
                                Logger.LogException("Watchdog.MonitorAppAsync (gravar trava)", ex);
                            }
                            AppPresence.ClearAlive(); // incidente tratado: não punir de novo se o serviço reiniciar
                            pendingRelaunch = true;
                            attempts = 0;
                            lastAttemptUtc = DateTime.MinValue;
                        }
                    }

                    // Depois de 10 tentativas (ex.: ninguém logado no PC) espaça para 1 min, sem encher o log.
                    var retryEvery = attempts < 15 ? 2 : 30;
                    if (pendingRelaunch && (DateTime.UtcNow - lastAttemptUtc).TotalSeconds >= retryEvery)
                    {
                        lastAttemptUtc = DateTime.UtcNow;
                        _lastAppLaunchRequest = DateTime.UtcNow;
                        attempts++;
                        LaunchApp(attempts);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogException("Watchdog.MonitorAppAsync", ex);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Reabre o app IMEDIATAMENTE. Antes a 1ª tentativa ia só pelo Agendador de Tarefas, que pode
    /// devolver "sucesso" sem abrir nada (tarefa apontando para um caminho antigo, desabilitada...) e
    /// a abertura direta só vinha 6 s depois. Agora: tentativas ímpares abrem DIRETO na sessão do
    /// usuário (token elevado) e usam o Agendador só se isso falhar; as pares fazem o inverso.
    /// Cada resultado vai para o log.
    /// </summary>
    private static void LaunchApp(int attempt)
    {
        var state = StateManager.Load();
        var sibling = SiblingAppExePath();
        var exe = ResolveAppExe(state);

        bool Direct()
        {
            if (exe == null) { Logger.Log("Reabertura direta: SharkBlocker.exe não encontrado (" + sibling + ")."); return false; }
            var ok = UserSessionLauncher.Launch(exe, "--minimized");
            Logger.Log($"Reabertura direta (tentativa {attempt}): {exe} -> {(ok ? "OK" : "FALHOU")}");
            return ok;
        }

        bool Scheduler()
        {
            var ok = AppStartupManager.RunNow();
            Logger.Log($"Reabertura pelo Agendador (tentativa {attempt}) -> {(ok ? "pedido aceito" : "FALHOU")}");
            return ok;
        }

        if (attempt % 2 == 1) { if (!Direct()) Scheduler(); }
        else { if (!Scheduler()) Direct(); }
    }

    private static string SiblingAppExePath() =>
        Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), "App", "SharkBlocker.exe");

    private static string? ResolveAppExe(SharkBlocker.Models.BlockState state)
    {
        var sibling = SiblingAppExePath();
        return File.Exists(sibling) ? sibling
             : (!string.IsNullOrEmpty(state.AutoStartExePath) && File.Exists(state.AutoStartExePath) ? state.AutoStartExePath! : null);
    }

    // ---- Uma cópia do app POR SESSÃO ativa (auditoria U4) ----
    // Antes só a sessão do console era atendida e bastava QUALQUER SharkBlocker.exe vivo para "estar tudo
    // certo": com troca rápida de usuário (ou uma conta nova), a outra sessão ficava sem trava de tela e
    // sem o vigia da barra de endereço. Agora cada sessão ativa precisa do seu app, e a reabertura é
    // tentada a cada ~5 s (um ciclo) por sessão, com espera maior quando a tentativa falha.
    private static readonly TimeSpan SessionRelaunchGap = TimeSpan.FromSeconds(4);
    private static readonly Dictionary<uint, SessionLaunchInfo> SessionLaunches = new();
    private static bool _noAppExeLogged;
    private static int _autoStartUpgradeTries; // recriações da tarefa antiga (limitadas por execução)

    private sealed class SessionLaunchInfo
    {
        public DateTime LastAttemptUtc;
        public DateTime BackoffUntilUtc;
        public int Failures;
    }

    private static HashSet<uint> SessionsWithApp()
    {
        var set = new HashSet<uint>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("SharkBlocker"))
        {
            try { set.Add((uint)p.SessionId); }
            catch { /* processo acabou de sair */ }
            finally { p.Dispose(); }
        }
        return set;
    }

    /// <summary>
    /// Garante o app em cada sessão ativa. Devolve false se não conseguiu enumerar sessões (aí o
    /// chamador usa o caminho antigo, só do console). Conta padrão: o app exige administrador, então o
    /// Windows recusa (erro 740) — fica registrado e a nova tentativa só vem depois de 5 min.
    /// </summary>
    private static bool EnsureAppInEveryActiveSession(SharkBlocker.Models.BlockState state)
    {
        var sessions = UserSessionLauncher.GetActiveUserSessions();
        if (sessions.Count == 0) return false;

        foreach (var gone in SessionLaunches.Keys.Where(k => !sessions.Contains(k)).ToList())
            SessionLaunches.Remove(gone);

        var exe = ResolveAppExe(state);
        if (exe == null)
        {
            if (!_noAppExeLogged)
            {
                _noAppExeLogged = true;
                Logger.Log("Reabertura por sessão: SharkBlocker.exe não encontrado (" + SiblingAppExePath() + ").");
            }
            return true;
        }

        var withApp = SessionsWithApp();
        var now = DateTime.UtcNow;
        foreach (var session in sessions)
        {
            if (withApp.Contains(session)) { SessionLaunches.Remove(session); continue; }

            if (!SessionLaunches.TryGetValue(session, out var info))
                SessionLaunches[session] = info = new SessionLaunchInfo();
            if (now < info.BackoffUntilUtc || now - info.LastAttemptUtc < SessionRelaunchGap) continue;
            info.LastAttemptUtc = now;

            Logger.Log($"Sessão {session} sem o app — abrindo (reabertura por sessão).");
            if (UserSessionLauncher.LaunchInSession(session, exe, "--minimized", out var error))
            {
                info.Failures = 0;
                continue; // o próximo ciclo confirma se ele ficou de pé
            }

            info.Failures++;
            if (error == UserSessionLauncher.ErrorNoToken)
            {
                info.Failures = 0;
                info.BackoffUntilUtc = now.AddSeconds(5); // tela de login: ninguém para receber o app ainda
            }
            else if (error == UserSessionLauncher.ErrorElevationRequired)
            {
                // LIMITE HONESTO: conta padrão não consegue rodar o app (exige administrador). hosts,
                // políticas e processos continuam protegidos pelo Watchdog; só a trava de tela e o vigia
                // da barra de endereço não existem nessa sessão. (Abrir o app como SYSTEM na sessão do
                // usuário foi descartado de propósito: seria escalada de privilégio.)
                info.BackoffUntilUtc = now.AddMinutes(5);
            }
            else
            {
                info.BackoffUntilUtc = now.AddSeconds(Math.Min(30 * info.Failures, 300));
                var ok = AppStartupManager.RunNow(); // plano B pelo Agendador de Tarefas
                Logger.Log($"Reabertura da sessão {session} pelo Agendador -> {(ok ? "pedido aceito" : "FALHOU")}");
            }
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _cycleCount++;
                var state = StateManager.Load();

                // Bloqueio agendado: se estamos dentro de uma janela, liga (ou estende) o bloqueio sem
                // ninguém precisar iniciar à mão. Idempotente e atômico (mesmo mutex do estado).
                if (BlockScheduler.EnsureActive()) state = StateManager.Load();

                // --- Camada permanente ---
                if (state.PermanentProtectionsEnabled)
                {
                    HostsFileManager.ApplySafeSearchEntries(SafeSearchDomains.GetHostsEntries());
                    BrowserPolicyManager.ApplyPermanentChromiumPolicies();
                    if (Directory.Exists(ExtensionFolderPath))
                        ExtensionInstaller.Install(ExtensionFolderPath);
                }

                // --- Pasta de instalação: protegida SEMPRE (não depende das proteções permanentes) ---
                // A cada ~60 s. Se a negação de exclusão já tinha sido aplicada e SUMIU, alguém mexeu
                // no ACL na mão (icacls/takeown): trava a tela e reaplica.
                if (_cycleCount % 12 == 1)
                {
                    if (_folderProtectedOnce && !FolderProtection.IsProtected(InstallRootPath))
                    {
                        Logger.Log("ADULTERAÇÃO: a proteção da pasta de instalação foi removida — travando a tela por 30 min e reaplicando.");
                        StateManager.StartScreenLockIfNotActive(StateManager.ScreenLockPenalty, AppPresence.ReasonTamper);
                        _lastAppLaunchRequest = DateTime.MinValue; // deixa o relançamento abaixo agir já
                    }
                    FolderProtection.Protect(InstallRootPath);
                    _folderProtectedOnce = FolderProtection.IsProtected(InstallRootPath);

                    // A tarefa que religa este serviço se ele for PARADO (sc stop / aba Serviços não
                    // dispara a recuperação automática): se sumiu, recria.
                    AppStartupManager.EnsureReviveTask();

                    // Reaplica a política de auto-restart do SCM (ver comentário no método) — cobre
                    // o caso de alguém desligá-la na aba "Recuperação" do services.msc sem parar o
                    // serviço nem apagar a tarefa de reanimação acima.
                    ServiceInstaller.EnsureFailureActionsConfigured();

                    // Reafirma as chaves SafeBoot (Minimal e Network) que fazem o Windows carregar este
                    // serviço no modo seguro. Idempotente (só grava se faltar/diferir): se alguém as
                    // apagar para "burlar" o Watchdog no próximo boot em modo seguro, elas voltam.
                    ServiceInstaller.EnsureSafeBootRegistration();

                    // U4: nega "Parar" a Administradores no serviço (sc stop / services.msc). Idempotente: só chama
                    // o sc.exe se o descritor de segurança perdeu a negação. Pausa, atualização e desinstalação
                    // (todas atrás de senha) restauram o descritor padrão antes de parar o serviço.
                    ServiceInstaller.EnsureStopProtection();

                    // Se a tarefa de abertura automática foi apagada — ou é a versão antiga, presa a um único
                    // usuário —, recria no formato novo (grupo Users: vale para qualquer conta).
                    if (!string.IsNullOrEmpty(state.AutoStartExePath) && File.Exists(state.AutoStartExePath))
                    {
                        if (!AppStartupManager.IsRegistered())
                        {
                            Logger.Log("A tarefa de abertura automática sumiu — recriando.");
                            AppStartupManager.RegisterAutoStart(state.AutoStartExePath, state.AutoStartUser);
                        }
                        else if (_autoStartUpgradeTries < 3 && !AppStartupManager.IsRegisteredForAllUsers())
                        {
                            _autoStartUpgradeTries++;
                            Logger.Log("A tarefa de abertura automática é a versão antiga (só um usuário) — recriando para todas as contas.");
                            AppStartupManager.RegisterAutoStart(state.AutoStartExePath, state.AutoStartUser);
                        }
                    }
                }

                // --- Trava de tela pendente / camada de desktop ---
                // O Watchdog roda sem tela (sessão 0): quem DESENHA a trava e quem VIGIA a barra de
                // endereço do navegador (BrowserUrlWatcher) é o app. Se há trava ativa OU proteções
                // permanentes ligadas e o app não está aberto (ex.: foi fechado pela bandeja),
                // pede ao Agendador de Tarefas pra abri-lo na sessão do usuário. Sem isso, "Sair"
                // na bandeja desligaria a camada de desktop.
                if (state.IsScreenLockActive || state.PermanentProtectionsEnabled || state.IsLocked)
                {
                    // U4: o app precisa estar em CADA sessão ativa (troca rápida de usuário, conta nova).
                    if (!EnsureAppInEveryActiveSession(state) &&
                        (DateTime.UtcNow - _lastAppLaunchRequest).TotalSeconds > 20 &&
                        !IsAppRunning())
                    {
                        // Não deu para enumerar as sessões: caminho antigo (só o console).
                        _lastAppLaunchRequest = DateTime.UtcNow;
                        Logger.Log("Trava de tela ativa e o app não está rodando — pedindo ao Agendador pra abri-lo.");
                        LaunchApp(1); // abre direto na sessão do usuário (Agendador só de reserva)
                    }
                }

                // --- Camada temporizada ---
                if (state.IsLocked)
                {
                    StatsStore.TickBlocked(DateTime.UtcNow); // estatística: tempo com o bloqueio ativo

                    var domains = (state.BlockedWebsites ?? new List<string>())
                        .Concat(state.ImportedWebsites ?? new List<string>())
                        .ToList();

                    HostsFileManager.ApplyBlockedSites(domains, state.HostsSubdomainPrefixes);
                    ProcessBlocker.KillBlockedProcesses(state.BlockedProcesses, state.BlockedProcessPathKeywords);

                    // Backlog #10: regra de firewall (saída bloqueada) para os executáveis encerrados e as VPNs instaladas.
                    FirewallBlocker.EnforceWhileLocked(ProcessBlocker.TakeKilledPaths());

                    // Auditoria U2: VPN nativa do Windows (RAS) — o Watchdog (SYSTEM) cobre as conexões "para todos
                    // os usuários"; o app, na sessão do usuário, cobre as "só deste usuário". Nunca lança.
                    NativeVpnGuard.DisconnectActiveVpnConnections("Watchdog");

                    // Idempotente: a cada ciclo (só grava se alguém apagou/alterou o valor no meio do bloqueio).
                    BrowserPolicyManager.ApplyRestrictions();
                    _lockRestrictionsApplied = true;

                    // Log por ciclo (não só heartbeat de 60s) enquanto travado —
                    // um bloqueio dura pouco tempo (minutos), então queremos
                    // visibilidade fina o bastante pra reconstruir a linha do
                    // tempo caso o bloqueio pare de funcionar antes da hora.
                    var stillThere = HostsFileManager.ContainsOurBlock(domains);
                    var remaining = state.LockedUntilUtc!.Value - DateTime.UtcNow;
                    Logger.Log($"[ciclo #{_cycleCount}] LOCKED restante={remaining:hh\\:mm\\:ss} domínios={domains.Count} " +
                               $"marcaAindaNoHosts={stillThere}");
                }
                else
                {
                    StatsStore.EndBlocked(); // grava o que sobrou do tempo bloqueado (no-op se nada pendente)

                    if (state.LockedUntilUtc.HasValue)
                    {
                        Logger.Log($"[ciclo #{_cycleCount}] Lock expirou (LockedUntilUtc={state.LockedUntilUtc:o}, agora={DateTime.UtcNow:o}) — limpando hosts.");
                        HostsFileManager.ClearBlockedSites();
                        state.LockedUntilUtc = null;
                        StateManager.Save(state);
                    }

                    // Auditoria U5: com a opção "anônimo sempre bloqueado" (e proteções permanentes ligadas) o
                    // bloqueio do modo anônimo continua depois do fim do bloqueio temporizado.
                    if (BrowserPolicyManager.IncognitoBlockedAlways(state))
                    {
                        BrowserPolicyManager.ApplyRestrictions();
                        _lockRestrictionsApplied = true; // se a opção for desligada depois, a próxima volta remove
                    }
                    else if (_lockRestrictionsApplied)
                    {
                        BrowserPolicyManager.RemoveRestrictions();
                        _lockRestrictionsApplied = false;
                    }

                    // Backlog #10: acabou o bloqueio, as regras de firewall do bloqueio saem (barato se não há nenhuma).
                    FirewallBlocker.RemoveIfActive();
                }

                // Só protege o hosts quando há algo de verdade sendo aplicado
                // (bloqueio ativo ou proteções permanentes). Antes disso rodava
                // sempre, incondicionalmente — e isso, combinado com uma falha
                // separada no ResetAcl, deixou o hosts num estado quebrado que
                // impedia QUALQUER acesso à internet, mesmo sem nada travado.
                // Proteger um arquivo que não tem nada pra proteger não traz
                // benefício real e só aumenta a superfície de risco.
                if (state.PermanentProtectionsEnabled || state.IsLocked)
                {
                    HostsFileManager.Protect();
                }
                else
                {
                    HostsFileManager.Unprotect();
                }

                // Backlog #9: filtro de DNS local (opção desligada por padrão). Rápido: o trabalho roda em segundo plano.
                DnsFilterController.Tick(state);

                // Heartbeat a cada ~60s (ciclo de 5s x 12), só pra confirmar no log
                // que o serviço está vivo e rodando mesmo sem bloqueio ativo.
                if (_cycleCount % 12 == 0)
                {
                    Logger.Log($"Watchdog vivo. Locked={state.IsLocked} PermanentProtections={state.PermanentProtectionsEnabled}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha em um ciclo de enforcement, tentando de novo no próximo.");
                Logger.LogException("Watchdog.EnforcementWorker.ExecuteAsync", ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try { _monitorCts.Cancel(); } catch { }
        AppPresence.MarkWatchdogStopped();
        ProcessSync.StopStatusPipeServer();
        ProcessSync.ReleaseWatchdogAlive();
        // Backlog #9: devolve o DNS do Windows ao normal antes de sair (senão ficaria em 127.0.0.1 sem ninguém escutando).
        try { DnsFilterController.Shutdown(); } catch (Exception ex) { Logger.LogException("Watchdog.StopAsync (DnsFilter)", ex); }
        try { _hostsWatcher?.Dispose(); } catch (Exception ex) { Logger.LogException("Watchdog.StopAsync (HostsWatcher)", ex); }
        try { _stateServer?.Dispose(); } catch (Exception ex) { Logger.LogException("Watchdog.StopAsync (LocalStateServer)", ex); }
        try { _processWatcher?.Dispose(); } catch (Exception ex) { Logger.LogException("Watchdog.StopAsync (ProcessStartWatcher)", ex); }
        return base.StopAsync(cancellationToken);
    }
}
