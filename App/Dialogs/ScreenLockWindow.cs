using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SharkBlocker.Models;
using SharkBlocker.Services;

namespace SharkBlocker.Dialogs;

// OBS sobre nomes: o projeto usa WPF + WinForms ao mesmo tempo (NotifyIcon da
// bandeja), e o SDK adiciona "using System.Drawing" e "using System.Windows.Forms"
// globalmente. Por isso nomes como Color, Brushes, FontFamily, Orientation e
// HorizontalAlignment ficam AMBÍGUOS aqui — este arquivo os qualifica por
// completo. (A versão anterior não compilava, entre outros motivos, por isso
// e por ter o namespace "SharkBlocker.App.Dialogs", que colide com a classe
// SharkBlocker.App.)

/// <summary>
/// Trava TODAS as telas por um tempo, com uma contagem regressiva:
///  - uma janela preta em tela cheia, sempre no topo, em CADA monitor;
///  - hook global de teclado que engole todas as teclas (Alt+Tab, Win, Alt+F4,
///    Ctrl+Shift+Esc, etc.);
///  - o Gerenciador de Tarefas fica desativado enquanto durar (ver LockdownPolicies);
///  - o fim é por HORÁRIO (UTC) e não por contagem de "ticks": suspender o PC
///    ou atrasar o timer não estica nem encurta a espera.
///
/// LIMITES HONESTOS:
///  - Ctrl+Alt+Del não pode ser bloqueado por programa nenhum. Ele ainda abre a
///    tela do Windows com "Sair" / "Trocar usuário" / botão de energia. Sair ou
///    reiniciar NÃO zera a espera: o término fica gravado (assinado) e o app
///    retoma a trava, só pelo tempo que faltava, na próxima abertura.
///  - Desligar o PC no botão sempre funciona (de propósito: é a válvula de
///    segurança caso algo dê errado), e a espera continua correndo enquanto ele
///    está desligado.
/// </summary>
public static class ScreenLock
{
    /// <summary>Teto de segurança: mesmo que o horário gravado esteja errado, nunca trava por mais que isso.</summary>
    private static readonly TimeSpan MaxDuration = SharkBlocker.Services.StateManager.ScreenLockPenalty + TimeSpan.FromMinutes(15);

    /// <summary>
    /// Teto da trava do BLOQUEIO AGENDADO: o fim vem da própria agenda (não de um horário gravado), e uma
    /// janela pode durar quase 24 h; janelas encadeadas somam até 48 h (ver BlockScheduler.MaxChain).
    /// </summary>
    private static readonly TimeSpan ScheduledMaxDuration = TimeSpan.FromHours(72);

    private static bool _running;

    /// <summary>true enquanto uma trava está na tela (evita abrir duas ao mesmo tempo).</summary>
    public static bool IsShowing => _running;

    public const string ReasonUninstall = "uninstall";
    public const string ReasonSearch = "search";
    public const string ReasonImage = "image";
    public const string ReasonRemoval = "removal";
    public const string ReasonSchedule = "schedule";
    public const string ReasonClosed = AppPresence.ReasonClosed;
    public const string ReasonTamper = AppPresence.ReasonTamper;

    /// <summary>
    /// Botão de urgência: a própria pessoa pede a trava (30 min, sem senha). NÃO é punição: não zera a sequência
    /// (StatsStore.KindFor devolve null para este motivo) e a trava tem a cena suave, sem a Ellen brava.
    /// Só reforça a proteção: não há como usá-lo para afrouxar nada.
    /// </summary>
    public const string ReasonUrgency = "urgency";
    private const string UrgencyHeadline = "RESPIRA";

    /// <summary>Duração das duas travas: tentar desinstalar e buscar algo inapropriado.</summary>
    public static readonly TimeSpan PenaltyDuration = SharkBlocker.Services.StateManager.ScreenLockPenalty;

    public static (string Headline, string Message) TextFor(string? reason) => reason switch
    {
        ReasonSearch => ("EXPEDIENTE ENCERRADO",
            EllenEventService.CaughtLine() + "\n\n" +
            "A Ellen flagrou uma busca ou página proibida: conteúdo inapropriado, " +
            "um termo da sua lista ou um proxy/VPN.\n\n" +
            "Por 30 minutos a tela fica travada. Respire, levante, beba água, coma algo doce.\n\n" +
            "Quando a tela destravar, o diário (aba Status) está lá se quiser anotar o que aconteceu."),
        ReasonImage => ("EXPEDIENTE ENCERRADO",
            EllenEventService.CaughtLine() + "\n\n" +
            "A Ellen viu uma imagem que parece conteúdo adulto na sua tela. Pode ter sido um falso alarme, mas ela prefere errar para o lado seguro.\n\n" +
            "A tela fica travada: 5 minutos na 1ª vez, 15 na 2ª e 30 da 3ª em diante (a contagem zera depois de 24 h sem novos casos). " +
            "Respire, levante, beba água, coma algo doce.\n\n" +
            "Quando a tela destravar, o diário (aba Status) está lá se quiser anotar o que aconteceu."),
        ReasonUrgency => (UrgencyHeadline,
            "Você pediu uma pausa e eu estou aqui. Hmph... não que eu estivesse esperando por isso.\n\n" +
            "Por 30 minutos a tela fica travada. Respire fundo, levante, beba água, lave o rosto. " +
            "Isso não conta como tentativa de burlar: a sua sequência continua intacta.\n\n" +
            "Se estiver pesado de verdade, fale com alguém de confiança. No Brasil, o CVV atende 24 h pelo 188."),
        ReasonClosed => ("ELLEN IS WATCHING",
            "O SharkBlocker foi encerrado fora do jeito normal (Gerenciador de Tarefas ou outro meio).\n\n" +
            "Por 30 minutos a tela fica travada. Fechar pela janela só minimiza; para sair de verdade " +
            "existe o menu da bandeja, que pede senha e confirmações."),
        ReasonTamper => ("ELLEN IS WATCHING",
            "Alguém mexeu nas proteções do SharkBlocker (permissões da pasta de instalação).\n\n" +
            "Por 30 minutos a tela fica travada. As proteções já foram reaplicadas."),
        ReasonSchedule => ("ELLEN IS WATCHING",
            "Você pediu para ALTERAR os bloqueios agendados.\n\n" +
            "Por 30 minutos a tela fica travada. Depois disso, se ainda quiser mudar, vai precisar da senha. " +
            "A agenda continua como está até lá."),
        ReasonRemoval => ("ELLEN IS WATCHING",
            "Você tentou REMOVER algo que está na lista de bloqueio.\n\n" +
            "Por 30 minutos a tela fica travada. Depois disso, se ainda quiser mesmo remover, " +
            "vai precisar confirmar (senha e perguntas). Nada foi removido até agora."),
        _ => ("EXPEDIENTE ENCERRADO",
            "Você pediu para desinstalar o SharkBlocker.\n\n" +
            "Por 30 minutos a tela fica travada, para dar tempo de pensar com calma.\n" +
            "Quando o tempo acabar, você decide: continuar ou desistir."),
    };

    /// <summary>Texto da trava durante um bloqueio agendado; <paramref name="untilUtc"/> é o fim do bloqueio.</summary>
    public static (string Headline, string Message) ScheduledText(DateTime untilUtc)
    {
        var local = untilUtc.ToLocalTime();
        var when = local.Date == DateTime.Now.Date ? $"às {local:HH:mm}" : $"em {local:dd/MM} às {local:HH:mm}";
        return ("BLOQUEIO AGENDADO",
            $"Este é um horário de bloqueio que você mesmo agendou.\n\n" +
            $"A tela fica travada até o fim do bloqueio, {when}. Respire, descanse, durma.");
    }

    /// <summary>
    /// Se agora estamos dentro de uma janela de bloqueio AGENDADO (e o bloqueio está ativo), trava as telas até o
    /// FIM do bloqueio (o fim da janela, ou da cadeia de janelas coladas). Só retorna quando ele acabar.
    /// Sem persistência própria: o fim é recalculado da agenda, então reiniciar o PC ou reabrir o app volta à
    /// trava só pelo que falta — e quem chama (app ao abrir e a cada ciclo) a refaz se ela cair.
    /// Bloqueio iniciado à mão, fora de janela, NÃO trava a tela (só bloqueia sites/apps, como sempre).
    /// Precisa ser chamado na thread da interface (WPF).
    ///
    /// LIMITES HONESTOS: valem os mesmos da trava normal — Ctrl+Alt+Del não é bloqueável e o botão de desligar
    /// sempre funciona (válvula de segurança); a espera continua correndo com o PC desligado.
    /// </summary>
    public static void ShowScheduledBlockIfDue(BlockState state)
    {
        try
        {
            if (_running || !state.IsLocked) return;

            var until = BlockScheduler.ActiveUntilUtc(state.Schedules, DateTime.Now);
            if (until == null || until.Value <= DateTime.UtcNow.AddSeconds(2)) return;

            var (headline, message) = ScheduledText(until.Value);
            Logger.Log($"Trava de tela do bloqueio agendado — até {until.Value.ToLocalTime():dd/MM HH:mm}.");
            ShowUntil(until.Value, headline, message, ScheduledMaxDuration);
        }
        catch (Exception ex)
        {
            Logger.LogException("ScreenLock.ShowScheduledBlockIfDue", ex);
        }
    }

    /// <summary>
    /// Trava as telas até <paramref name="untilUtc"/> e só retorna quando acabar.
    /// Precisa ser chamado na thread da interface (WPF).
    /// </summary>
    public static void ShowUntil(DateTime untilUtc, string headline, string message, TimeSpan? maxDuration = null)
    {
        if (_running) return;
        if (untilUtc <= DateTime.UtcNow) return;

        var capped = DateTime.UtcNow + (maxDuration ?? MaxDuration);
        if (untilUtc > capped) untilUtc = capped;

        _running = true;
        try
        {
            new Session(untilUtc, headline, message).Run();
        }
        catch (Exception ex)
        {
            Logger.LogException("ScreenLock.ShowUntil", ex);
        }
        finally
        {
            _running = false;
        }
    }

    // ---------- Sessão (uma trava inteira, todas as telas) ----------

    private sealed class Session
    {
        private const int WH_KEYBOARD_LL = 13;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        // Estático e guardado em campo: se o delegate for coletado pelo GC com o
        // hook ainda instalado, o Windows chama um ponteiro morto e o app cai.
        private static LowLevelKeyboardProc? _hookProc;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private readonly DateTime _untilUtc;
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private readonly string _headline;
        private readonly string _message;

        // ---- Cenas da trava ----
        // A 1ª cena é SEMPRE a Ellen brava (ellen-angry.png). Depois, a cada SceneSeconds a imagem
        // troca com fade para outra expressão da Ellen. Tudo é calculado pelo HORÁRIO decorrido
        // (não por contagem de ticks), e é igual em todos os monitores.
        public const int SceneSeconds = 120;
        private const string AngryImage = "ellen-angry.png";
        private static readonly string[] OtherImages =
        {
            "ellen-calm.png", "ellen-calm-2.png", "ellen-calm-3.png", "ellen-calm-4.png",
            "ellen-determined.png", "ellen-determined-2.png", "ellen-determined-3.png",
            "ellen-sweet.png", "ellen-sweet-2.png",
            "ellen-warm.png", "ellen-warm-2.png", "ellen-warm-3.png",
            "ellen-smirk.png", "ellen-smirk-2.png", "ellen-smirk-3.png", "ellen-smirk-4.png", "ellen-smirk-5.png",
            "ellen-shy.png", "ellen-shy-2.png",
        };
        private readonly string[] _sceneOrder;
        // Trava pedida pelo botão de urgência: nenhuma cena "brava" (nem a imagem, nem as frases).
        private readonly bool _gentle;
        private readonly Dictionary<int, string> _sceneTexts = new();
        private int _currentScene;

        public string SceneImage(int scene)
        {
            if (_gentle) return _sceneOrder[Math.Max(scene, 0) % _sceneOrder.Length];
            return scene <= 0 ? AngryImage : _sceneOrder[(scene - 1) % _sceneOrder.Length];
        }

        public string SceneText(int scene)
        {
            if (!_sceneTexts.TryGetValue(scene, out var text))
            {
                text = scene <= 0 && !_gentle ? MotivationalQuotes.PickAngry() : MotivationalQuotes.Pick();
                _sceneTexts[scene] = text;
            }
            return text;
        }
        public int BackdropIndex { get; } = Random.Shared.Next(2); // mesma arte em todos os monitores
        private readonly List<LockWindow> _windows = new();

        private DispatcherTimer? _timer;
        private DispatcherFrame? _frame;
        private IntPtr _hookHandle = IntPtr.Zero;
        private bool _finished;

        public Session(DateTime untilUtc, string headline, string message)
        {
            _untilUtc = untilUtc;
            _headline = headline;
            _message = message;
            _gentle = headline == UrgencyHeadline;
            // Na trava suave ficam só as expressões acolhedoras (sem as "smirk"/"determined").
            var pool = _gentle
                ? OtherImages.Where(f => !f.Contains("smirk") && !f.Contains("determined")).ToArray()
                : OtherImages;
            _sceneOrder = pool.OrderBy(_ => Random.Shared.Next()).ToArray();
        }

        public bool Finished => _finished;

        public void Run()
        {
            try
            {
                LockdownPolicies.DisableTaskManager();
                InstallKeyboardHook();

                var screens = System.Windows.Forms.Screen.AllScreens;
                var primary = System.Windows.Forms.Screen.PrimaryScreen;

                foreach (var screen in screens)
                {
                    var isPrimary = primary != null && screen.DeviceName == primary.DeviceName;
                    var window = new LockWindow(this, screen, _headline, _message, isPrimary);
                    _windows.Add(window);
                    window.Show();
                }

                _windows.FirstOrDefault(w => w.IsPrimaryScreen)?.Activate();

                UpdateCountdown();

                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _timer.Tick += (_, _) => OnTick();
                _timer.Start();

                // Espera (sem travar a bomba de mensagens) até a trava terminar.
                _frame = new DispatcherFrame();
                Dispatcher.PushFrame(_frame);
            }
            finally
            {
                Cleanup();
            }
        }

        private void OnTick()
        {
            if (_finished) return;

            if (DateTime.UtcNow >= _untilUtc)
            {
                Finish();
                return;
            }

            UpdateCountdown();

            // Troca de cena (com fade) a cada SceneSeconds.
            var scene = (int)((DateTime.UtcNow - _startedUtc).TotalSeconds / SceneSeconds);
            if (scene != _currentScene)
            {
                _currentScene = scene;
                foreach (var w in _windows) w.ShowScene(scene);
            }

            // Mantém as janelas por cima de tudo (ex.: algo que tenha roubado o foco).
            foreach (var w in _windows) w.KeepOnTop();
        }

        private void UpdateCountdown()
        {
            var remaining = _untilUtc - DateTime.UtcNow;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            var totalSeconds = (int)Math.Ceiling(remaining.TotalSeconds);
            // Bloqueio agendado pode durar horas: a partir de 1 h mostra H:MM:SS (senão seria "480:00").
            var text = totalSeconds >= 3600
                ? $"{totalSeconds / 3600}:{totalSeconds % 3600 / 60:D2}:{totalSeconds % 60:D2}"
                : $"{totalSeconds / 60}:{totalSeconds % 60:D2}";

            var total = (_untilUtc - _startedUtc).TotalSeconds;
            var fraction = total > 0 ? 1 - remaining.TotalSeconds / total : 1;

            foreach (var w in _windows)
            {
                w.SetCountdown(text, totalSeconds);
                w.SetProgress(fraction);
            }
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            if (_frame != null) _frame.Continue = false;
        }

        private void Cleanup()
        {
            _finished = true;

            try { _timer?.Stop(); } catch { }
            _timer = null;

            RemoveKeyboardHook();

            foreach (var w in _windows)
            {
                try { w.CloseForReal(); } catch { }
            }
            _windows.Clear();

            LockdownPolicies.RestoreTaskManager();
        }

        // ---------- Hook global de teclado ----------

        private void InstallKeyboardHook()
        {
            try
            {
                _hookProc = HookCallback;
                _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
                if (_hookHandle == IntPtr.Zero)
                {
                    Logger.Log($"ScreenLock: SetWindowsHookEx falhou (erro {Marshal.GetLastWin32Error()}). " +
                               "A trava continua, mas só com o bloqueio local das janelas.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogException("ScreenLock.InstallKeyboardHook", ex);
            }
        }

        private void RemoveKeyboardHook()
        {
            try
            {
                if (_hookHandle != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hookHandle);
                    _hookHandle = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Logger.LogException("ScreenLock.RemoveKeyboardHook", ex);
            }
            finally
            {
                _hookProc = null;
            }
        }

        // Engole TODA tecla (pressionar e soltar) enquanto a trava existir.
        // Precisa ser rápido: o Windows desiste de hooks lentos.
        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0) return (IntPtr)1;
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
    }

    // ---------- Uma janela preta, por monitor ----------

    private sealed class LockWindow : Window
    {
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        private readonly Session _session;
        private readonly System.Drawing.Rectangle _bounds;
        private readonly TextBlock _countdown;
        private readonly System.Windows.Controls.Border _progressFill;
        private readonly ScaleTransform _progressScale = new(0, 1);
        private bool _allowClose;

        // Slot da frase (troca com fade) e palco da imagem (troca com crossfade).
        private System.Windows.Controls.Grid _quoteSlot = new();
        private double _quoteSize = 24;
        private System.Windows.Controls.Grid _stage = new();
        private System.Windows.Controls.Image? _stageImage;

        public bool IsPrimaryScreen { get; }

        public LockWindow(Session session, System.Windows.Forms.Screen screen, string headline, string message, bool isPrimary)
        {
            _session = session;
            _bounds = screen.Bounds;
            IsPrimaryScreen = isPrimary;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _bounds.X;
            Top = _bounds.Y;
            Width = _bounds.Width;
            Height = _bounds.Height;
            Background = new RadialGradientBrush(
                System.Windows.Media.Color.FromRgb(38, 15, 28),   // centro: vinho escuro
                System.Windows.Media.Color.FromRgb(9, 8, 13))     // bordas: quase preto
            {
                Center = new System.Windows.Point(0.5, 0.45),
                RadiusX = 0.85,
                RadiusY = 0.85
            };
            Cursor = System.Windows.Input.Cursors.None;
            ForceCursor = true;

            var panel = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Vertical,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(50)
            };

            var glyph = LoadGlyph(isPrimary ? 84 : 56);
            if (glyph != null)
                panel.Children.Add(glyph);
            else
                panel.Children.Add(MakeText("🦈", isPrimary ? 96 : 64, FontWeights.Normal, System.Windows.Media.Color.FromRgb(255, 107, 142), new Thickness(0, 0, 0, 20)));

            if (isPrimary)
            {
                panel.Children.Add(MakeText(headline, 42, FontWeights.Bold, System.Windows.Media.Color.FromRgb(255, 77, 109), new Thickness(0, 0, 0, 22)));
                panel.Children.Add(MakeText(message, 21, FontWeights.Normal, System.Windows.Media.Color.FromRgb(201, 196, 211), new Thickness(0, 0, 0, 30)));
                AddQuoteSlot(panel, true);
            }
            else
            {
                panel.Children.Add(MakeText(headline, 30, FontWeights.Bold, System.Windows.Media.Color.FromRgb(255, 77, 109), new Thickness(0, 0, 0, 22)));
                AddQuoteSlot(panel, false);
            }

            _countdown = MakeText("", isPrimary ? 110 : 72, FontWeights.Black, System.Windows.Media.Color.FromRgb(255, 209, 220), new Thickness(0, 0, 0, 24));
            _countdown.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Courier New");
            panel.Children.Add(_countdown);

            if (isPrimary)
            {
                panel.Children.Add(MakeText(
                    "Nenhuma tecla ou clique funciona até o tempo acabar.\nReiniciar o computador não zera a espera.",
                    14, FontWeights.Normal, System.Windows.Media.Color.FromRgb(154, 148, 168), new Thickness(0, 0, 0, 0)));
            }

            // Entrada escalonada: cada bloco de texto aparece um pouco depois do anterior.
            // (Só visual: a trava e o bloqueio de teclado já estão ativos desde o primeiro frame.)
            var stagger = 0;
            foreach (UIElement child in panel.Children)
            {
                UiMotion.FadeSlideIn(child, 16, 520, stagger);
                stagger += 120;
            }

            // O painel fica centralizado; a imagem fica ancorada no canto inferior esquerdo.
            var root = new Grid();
            var backdrop = LoadBackdrop(session.BackdropIndex == 0 ? "bg-ice.jpg" : "bg-kitchen.jpg");
            if (backdrop != null) root.Children.Add(backdrop);
            // O painel de texto vai dentro de um Viewbox que só ENCOLHE (nunca amplia): se, num monitor baixo ou
            // estreito, o conteúdo não couber na área livre (tela menos o espaço da imagem), ele diminui por
            // inteiro em vez de ser cortado em cima/embaixo ou de passar por trás da imagem da Ellen.
            var fit = new Viewbox
            {
                Child = panel,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            root.Children.Add(fit);
            // Palco da imagem (canto inferior esquerdo). O tamanho acompanha a tela; a imagem da cena
            // (a Ellen brava primeiro, depois as outras) entra e sai por crossfade em ShowScene.
            _stage.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            _stage.VerticalAlignment = System.Windows.VerticalAlignment.Bottom;
            _stage.Margin = new Thickness(28, 0, 0, 24);
            _stage.IsHitTestVisible = false;
            root.SizeChanged += (_, e) =>
            {
                // A arte da Ellen brava é de CORPO INTEIRO (proporção ~0,43 de largura por altura) e vem com uma
                // pequena margem transparente: com a altura do palco sobrando, a imagem aparece inteira (sem cortar
                // cabeça nem botas) e a largura nunca passa de metade da altura.
                var stageHeight = e.NewSize.Height * (isPrimary ? 0.84 : 0.62);
                var stageWidth = Math.Min(Math.Min(e.NewSize.Width * (isPrimary ? 0.24 : 0.20), isPrimary ? 480 : 300),
                                          stageHeight * 0.5);
                _stage.Width = stageWidth;
                _stage.Height = stageHeight;

                // O painel de texto é centralizado: reserva dos DOIS lados o espaço do palco (+ folga), para a
                // imagem nunca cobrir o texto. Em tela estreita o texto quebra em mais linhas em vez de invadir.
                var reserve = _stage.Margin.Left + stageWidth + 16;
                fit.MaxWidth = Math.Max(360, e.NewSize.Width - 2 * reserve);
                fit.MaxHeight = Math.Max(240, e.NewSize.Height - 24); // folga para a assinatura e a barra do rodapé
                panel.MaxWidth = Math.Max(320, fit.MaxWidth - panel.Margin.Left - panel.Margin.Right); // o texto quebra aqui
            };
            // Balanço leve: a Ellen "respira" enquanto o tempo passa.
            var bob = new TranslateTransform();
            _stage.RenderTransform = bob;
            bob.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -12, TimeSpan.FromSeconds(1.8))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase()
            });
            root.Children.Add(_stage);
            ShowScene(0, animate: false);

            // Barra fina no rodapé: quanto da espera já passou.
            _progressFill = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 38, 77)),
                Height = 6,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                RenderTransformOrigin = new System.Windows.Point(0, 0.5),
                RenderTransform = _progressScale
            };
            root.Children.Add(_progressFill);

            // Assinatura discreta no rodapé, centralizada.
            root.Children.Add(new TextBlock
            {
                Text = "E L L E N   I S   W A T C H I N G",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 107, 142)),
                Opacity = 0.75,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 26),
                IsHitTestVisible = false
            });
            Content = root;
            Loaded += (_, _) => UiMotion.FadeIn(root, 450);

            // Toda entrada local é descartada.
            PreviewKeyDown += (_, e) => e.Handled = true;
            PreviewKeyUp += (_, e) => e.Handled = true;
            PreviewMouseDown += (_, e) => e.Handled = true;
            PreviewMouseUp += (_, e) => e.Handled = true;
            PreviewMouseWheel += (_, e) => e.Handled = true;

            // Se algo roubar o foco, pega de volta.
            Deactivated += (_, _) =>
            {
                if (!_session.Finished)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { if (!_session.Finished) Activate(); } catch { }
                    }));
                }
            };
        }

        // Frase motivacional num cartão translúcido, com a assinatura da Ellen e uma barra de destaque.
        private static System.Windows.Controls.Border MakeQuote(string quote, double size, Thickness margin, bool angry = false)
        {
            var text = MakeText("\u201C" + quote + "\u201D", angry ? size * 1.08 : size, angry ? FontWeights.SemiBold : FontWeights.Medium,
                angry ? System.Windows.Media.Color.FromRgb(255, 214, 218) : System.Windows.Media.Color.FromRgb(243, 241, 246),
                new Thickness(0, 0, 0, 12));

            var signature = MakeText("\u2014 Ellen", size * 0.58, FontWeights.SemiBold,
                System.Windows.Media.Color.FromRgb(255, 107, 142), new Thickness(0));

            var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
            content.Children.Add(text);
            content.Children.Add(signature);

            return new System.Windows.Controls.Border
            {
                Child = content,
                Margin = margin,
                Padding = new Thickness(36, 24, 36, 22),
                MaxWidth = 960,
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(angry
                    ? System.Windows.Media.Color.FromArgb(200, 58, 9, 16)
                    : System.Windows.Media.Color.FromArgb(150, 23, 21, 30)),
                BorderBrush = new SolidColorBrush(angry
                    ? System.Windows.Media.Color.FromArgb(230, 255, 64, 80)
                    : System.Windows.Media.Color.FromArgb(120, 255, 107, 142)),
                BorderThickness = new Thickness(angry ? 2 : 1),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
        }

        // Tubarão (Assets\shark-glyph.png) no topo da trava. Se não carregar, o chamador usa o emoji.
        private static System.Windows.Controls.Image? LoadGlyph(double size)
        {
            try
            {
                var b = new System.Windows.Media.Imaging.BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri("pack://application:,,,/Assets/shark-glyph.png", UriKind.Absolute);
                b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                b.EndInit();
                b.Freeze();
                return new System.Windows.Controls.Image
                {
                    Source = b,
                    Width = size,
                    Height = size,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 0, 22),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };
            }
            catch
            {
                return null;
            }
        }

        // Carrega a imagem do recurso embutido (Assets\lockscreen-mascot.png). Se não achar,
        // tenta o arquivo ao lado do .exe; se também falhar, a trava segue SEM a imagem
        // (nunca derruba a trava por causa de um enfeite).
        // Arte de fundo bem escurecida (a trava é para ser calma e legível, não chamativa).
        // Se a imagem não carregar, a trava segue com o degradê de sempre.
        private static System.Windows.Controls.Image? LoadBackdrop(string fileName)
        {
            try
            {
                var b = new System.Windows.Media.Imaging.BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri($"pack://application:,,,/Assets/{fileName}", UriKind.Absolute);
                b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                b.EndInit();
                b.Freeze();
                return new System.Windows.Controls.Image
                {
                    Source = b,
                    Stretch = Stretch.UniformToFill,
                    Opacity = 0.22,
                    IsHitTestVisible = false
                };
            }
            catch
            {
                return null;
            }
        }

        private static System.Windows.Controls.Image? LoadMascot(double width)
        {
            System.Windows.Media.Imaging.BitmapImage? bmp = null;
            var candidates = new[]
            {
                "pack://application:,,,/Assets/lockscreen-mascot.png",
                System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "lockscreen-mascot.png"),
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    var b = new System.Windows.Media.Imaging.BitmapImage();
                    b.BeginInit();
                    b.UriSource = new Uri(candidate, UriKind.Absolute);
                    b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    b.EndInit();
                    b.Freeze();
                    bmp = b;
                    break;
                }
                catch
                {
                    // tenta o próximo caminho
                }
            }

            if (bmp == null)
            {
                Logger.Log("ScreenLock: imagem Assets/lockscreen-mascot.png não encontrada — a trava segue sem ela.");
                return null;
            }

            return new System.Windows.Controls.Image
            {
                Source = bmp,
                Width = width,
                Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                Margin = new Thickness(28, 0, 0, 24),
                IsHitTestVisible = false,
            };
        }

        private void AddQuoteSlot(StackPanel panel, bool isPrimary)
        {
            _quoteSize = isPrimary ? 24 : 19;
            _quoteSlot = new System.Windows.Controls.Grid
            {
                Margin = new Thickness(0, 0, 0, isPrimary ? 34 : 24),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            panel.Children.Add(_quoteSlot); // preenchido por ShowScene
        }

        /// <summary>
        /// Mostra a cena <paramref name="index"/>: imagem + frase. A imagem nova entra por cima com fade
        /// enquanto a antiga some; a frase some e a nova aparece. Cena 0 = Ellen brava.
        /// Se as animações do Windows estiverem desligadas, a troca é instantânea.
        /// </summary>
        public void ShowScene(int index, bool animate = true)
        {
            try
            {
                var fade = animate && UiMotion.Enabled;
                var duration = TimeSpan.FromMilliseconds(1200);

                var bmp = LoadSceneBitmap(_session.SceneImage(index)) ?? LoadSceneBitmap("lockscreen-mascot.png");
                var oldImage = _stageImage;
                if (bmp != null)
                {
                    var img = new System.Windows.Controls.Image
                    {
                        Source = bmp,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                        IsHitTestVisible = false
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    _stage.Children.Add(img);
                    _stageImage = img;
                    if (fade)
                        img.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration)
                        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
                }

                if (oldImage != null)
                {
                    if (fade)
                    {
                        var fadeOut = new DoubleAnimation(1, 0, duration)
                        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
                        fadeOut.Completed += (_, _) => { try { _stage.Children.Remove(oldImage); } catch { } };
                        oldImage.BeginAnimation(OpacityProperty, fadeOut);
                    }
                    else
                    {
                        _stage.Children.Remove(oldImage);
                    }
                }

                SwapQuote(index, fade);
            }
            catch (Exception ex)
            {
                // Enfeite nunca pode derrubar a trava.
                Logger.LogException("ScreenLock.ShowScene", ex);
            }
        }

        private void SwapQuote(int index, bool fade)
        {
            var next = MakeQuote(_session.SceneText(index), _quoteSize, new Thickness(0), angry: index <= 0);
            var old = _quoteSlot.Children.Count > 0 ? _quoteSlot.Children[0] : null;

            void Show()
            {
                try
                {
                    _quoteSlot.Children.Clear();
                    _quoteSlot.Children.Add(next);
                    if (fade) UiMotion.FadeSlideIn(next, 10, 600);
                }
                catch { }
            }

            if (old == null || !fade)
            {
                Show();
                return;
            }

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(600));
            fadeOut.Completed += (_, _) => Show();
            old.BeginAnimation(OpacityProperty, fadeOut);
        }

        // Imagens das cenas, em cache (BitmapImage congelado). Tenta o recurso embutido e,
        // se faltar, o arquivo ao lado do .exe; se também falhar, devolve null.
        private static readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage?> SceneBitmaps = new();

        private static System.Windows.Media.Imaging.BitmapImage? LoadSceneBitmap(string fileName)
        {
            if (SceneBitmaps.TryGetValue(fileName, out var cached)) return cached;

            System.Windows.Media.Imaging.BitmapImage? bmp = null;
            var candidates = new[]
            {
                $"pack://application:,,,/Assets/{fileName}",
                System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", fileName),
            };
            foreach (var candidate in candidates)
            {
                try
                {
                    var b = new System.Windows.Media.Imaging.BitmapImage();
                    b.BeginInit();
                    b.UriSource = new Uri(candidate, UriKind.Absolute);
                    b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    b.EndInit();
                    b.Freeze();
                    bmp = b;
                    break;
                }
                catch
                {
                    // tenta o próximo caminho
                }
            }

            if (bmp == null) Logger.Log($"ScreenLock: imagem Assets/{fileName} não encontrada.");
            SceneBitmaps[fileName] = bmp;
            return bmp;
        }

        private static TextBlock MakeText(string text, double size, FontWeight weight, System.Windows.Media.Color color, Thickness margin) =>
            new()
            {
                Text = text,
                FontSize = size,
                FontWeight = weight,
                Foreground = new SolidColorBrush(color),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Display, Segoe UI"),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = margin,
                MaxWidth = 1100
            };

        public void SetProgress(double fraction)
        {
            _progressScale.ScaleX = Math.Clamp(fraction, 0, 1);
        }

        public void SetCountdown(string text, int totalSeconds)
        {
            _countdown.Text = text;

            if (totalSeconds <= 30)
                _countdown.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 77, 109));
            else if (totalSeconds <= 60)
                _countdown.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 154, 90));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Posiciona em PIXELS reais do monitor (evita erros de conversão de DPI
            // entre telas com escalas diferentes) e já deixa no topo.
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, HWND_TOPMOST, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, SWP_SHOWWINDOW);
        }

        public void KeepOnTop()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Alt+F4 e afins: só deixa fechar quando a sessão manda.
            if (!_allowClose) e.Cancel = true;
            base.OnClosing(e);
        }

        public void CloseForReal()
        {
            _allowClose = true;
            Close();
        }
    }
}
