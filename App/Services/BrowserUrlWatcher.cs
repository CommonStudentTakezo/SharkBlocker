using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using SharkBlocker.Dialogs;

namespace SharkBlocker.Services;

/// <summary>
/// CAMADA DE DESKTOP, independente da extensão. A cada ~1,5 s olha o navegador que está em
/// primeiro plano, lê a barra de endereço (UI Automation — a mesma interface dos leitores de
/// tela) e passa a URL pelo <see cref="UrlPolicy"/>. Se for conteúdo adulto, proxy/VPN/navegador
/// remoto ou um termo proibido digitado numa busca:
///   1) inicia a trava de tela de 30 min (a MESMA da desinstalação), e
///   2) fecha a janela do navegador (senão, quando a trava acabasse, a página ainda estaria lá e
///      travaria de novo, em laço).
///
/// Por que existe: com a extensão removida/desativada, o filtro do navegador some. Esta camada
/// roda no app, vale para Chrome, Edge, Brave, Opera, Vivaldi e Firefox, e continua funcionando
/// sem a extensão.
///
/// O Watchdog (serviço, sessão 0) NÃO enxerga a área de trabalho do usuário, por isso isto roda
/// no app. Para o app não poder ser simplesmente fechado, o Watchdog o reabre quando há proteções
/// permanentes ativas (ver EnforcementWorker).
///
/// LIMITES HONESTOS
///  - Só vê o navegador em PRIMEIRO PLANO e só o que aparece na barra de endereço. Se o navegador
///    esconder a consulta da barra (ou mostrar só o título), ela não é vista.
///  - Não vê navegadores que não expõem a barra por UI Automation, nem janelas "sem barra"
///    (modo app/kiosk).
///  - Enquanto você DIGITA na barra (campo com foco) nada é avaliado; vale a página já aberta.
///  - Ao ser consultado por UI Automation, o Chrome/Edge podem ligar o modo de acessibilidade,
///    com um custo pequeno de desempenho.
///  - Não impede um Administrador que encerre o app E o serviço (ver README, "Limite honesto").
/// </summary>
public static class BrowserUrlWatcher
{
    private const int PollMilliseconds = 1500;
    private const uint WM_CLOSE = 0x0010;

    private static readonly HashSet<string> ChromiumProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "opera", "vivaldi", "chromium",
    };

    private static readonly HashSet<string> FirefoxProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "firefox", "librewolf", "waterfox",
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly object StartGate = new();
    private static Thread? _thread;
    private static volatile bool _stop;

    /// <summary>Inicia o vigia (uma vez). Seguro de chamar de novo.</summary>
    public static void Start()
    {
        lock (StartGate)
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "BrowserUrlWatcher" };
            _thread.Start();
        }
    }

    public static void Stop() => _stop = true;

    private static void Loop()
    {
        Logger.Log("BrowserUrlWatcher iniciado: vigiando a barra de endereço do navegador em primeiro plano.");
        var errorLogs = 0;

        while (!_stop)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // Nunca derruba o app; registra só as primeiras falhas pra não encher o log.
                if (errorLogs++ < 5) Logger.LogException("BrowserUrlWatcher.Tick", ex);
            }

            Thread.Sleep(PollMilliseconds);
        }
    }

    private static void Tick()
    {
        var state = StateManager.Load();

        // Mesma regra do restante: só com proteções permanentes ligadas ou bloqueio ativo.
        if (!state.PermanentProtectionsEnabled && !state.IsLocked) return;

        // Já tem trava na tela (ou prestes a ter): nada a fazer.
        if (state.IsScreenLockActive || ScreenLock.IsShowing) return;

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        GetWindowThreadProcessId(hwnd, out var pid);
        // OTIMIZAÇÃO: Process.GetProcessById(...).ProcessName lista todos os processos do sistema a cada
        // ciclo; ler só o caminho do executável é um handle limitado.
        var imagePath = ProcessBlocker.GetImagePath((int)pid);
        if (string.IsNullOrEmpty(imagePath)) return; // o processo acabou de fechar (ou sem acesso)
        var processName = Path.GetFileNameWithoutExtension(imagePath);

        var isFirefox = FirefoxProcesses.Contains(processName);
        if (!isFirefox && !ChromiumProcesses.Contains(processName)) return;

        var raw = ReadAddressBar(hwnd, isFirefox);
        if (string.IsNullOrWhiteSpace(raw)) return;

        var text = raw.Trim();
        if (text.Any(char.IsWhiteSpace)) return; // texto de busca digitado, não um endereço
        var url = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;

        var reason = UrlPolicy.EvaluateTyped(url, state);
        if (reason == null || !UrlPolicy.LockReasons.Contains(reason)) return;

        OnViolation(hwnd, url, reason);
    }

    // OTIMIZAÇÃO: FindFirst(Descendants) percorre a árvore de UI inteira do navegador (cara). A barra
    // de endereço é a mesma enquanto a janela existe, então guarda o elemento e só procura de novo
    // se ele sumir (a leitura lança exceção) ou se o navegador/janela em primeiro plano mudar.
    private static AutomationElement? _cachedBar;
    private static IntPtr _cachedBarHwnd;
    private static bool _cachedBarFirefox;

    private static string? ReadAddressBar(IntPtr hwnd, bool firefox)
    {
        var bar = (_cachedBar != null && _cachedBarHwnd == hwnd && _cachedBarFirefox == firefox) ? _cachedBar : null;

        if (bar == null)
        {
            var window = AutomationElement.FromHandle(hwnd);

            // Chromium: a caixa de endereço tem a classe "OmniboxViewViews" em qualquer idioma.
            // Firefox: AutomationId "urlbar-input".
            bar = firefox
                ? window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"))
                : window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "OmniboxViewViews"));

            if (bar == null && !firefox) bar = FindAddressEditByName(window);
            if (bar == null) return null;

            _cachedBar = bar;
            _cachedBarHwnd = hwnd;
            _cachedBarFirefox = firefox;
        }

        try
        {
            // Campo com foco = a pessoa está digitando; espera ela confirmar.
            if (bar.GetCurrentPropertyValue(AutomationElement.HasKeyboardFocusProperty) is bool focused && focused)
                return null;

            if (!bar.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) return null;
            return ((ValuePattern)pattern).Current.Value;
        }
        catch
        {
            // Elemento ficou obsoleto (aba/janela recriada): descarta e procura de novo no próximo ciclo.
            _cachedBar = null;
            return null;
        }
    }

    // Plano B para builds do Chromium em que a classe mude: um campo de texto cujo nome lembra "endereço".
    private static AutomationElement? FindAddressEditByName(AutomationElement window)
    {
        var edits = window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

        foreach (AutomationElement edit in edits)
        {
            var name = edit.Current.Name ?? "";
            if (name.Contains("address", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("endere", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("direcc", StringComparison.OrdinalIgnoreCase))
            {
                return edit;
            }
        }
        return null;
    }

    private static void OnViolation(IntPtr hwnd, string url, string reason)
    {
        // Registra só o domínio (não a busca inteira) pra não gravar o que foi digitado.
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?";
        Logger.Log($"BrowserUrlWatcher: motivo='{reason}' em '{host}' — travando a tela e fechando a janela do navegador.");

        try
        {
            StateManager.StartScreenLockIfNotActive(ScreenLock.PenaltyDuration, ScreenLock.ReasonSearch);
        }
        catch (Exception ex)
        {
            Logger.LogException("BrowserUrlWatcher.OnViolation (trava)", ex);
        }

        try
        {
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Logger.LogException("BrowserUrlWatcher.OnViolation (fechar janela)", ex);
        }
    }
}
