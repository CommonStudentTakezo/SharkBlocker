using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using SharkBlocker.Services;

namespace SharkBlocker.Dialogs;

/// <summary>
/// Widget da Frase do Dia da Ellen na tela do PC. Padrão: canto inferior esquerdo (acima da barra de
/// tarefas). Dá para arrastar para qualquer canto de qualquer monitor (posição lembrada, ver
/// <see cref="EllenWidgetSettings"/>) e compactar para só o avatar. Quando a Ellen fala com o widget
/// compacto, ele expande sozinho durante a fala e volta ao compacto depois.
/// Fica por cima das outras janelas mas NÃO rouba o foco (WS_EX_NOACTIVATE) e não aparece no
/// Alt+Tab (WS_EX_TOOLWINDOW). É só enfeite: qualquer falha aqui é registrada e o app segue.
///
/// Modo Silêncio (<see cref="EllenSilenceService"/>): por 1 hora o widget some e a Ellen não puxa
/// assunto. Liga/desliga pelo botão da lua no widget ou pelo menu da bandeja.
/// </summary>
public partial class DesktopQuoteWindow : Window
{
    private static DesktopQuoteWindow? _instance;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int SM_CXDRAG = 68;
    private const int SM_CYDRAG = 69;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Cria e mostra o widget (uma vez). Chamar na thread da interface.</summary>
    public static void EnsureShown()
    {
        try
        {
            if (_instance == null)
            {
                _instance = new DesktopQuoteWindow();
                _instance.Closed += (_, _) => _instance = null;
            }
            if (!EllenSilenceService.IsActive) _instance.Show();
            _instance.ApplySilence();
        }
        catch (Exception ex)
        {
            Logger.LogException("DesktopQuoteWindow.EnsureShown", ex);
        }
    }

    /// <summary>Mostra/esconde (item do menu da bandeja).</summary>
    public static void Toggle()
    {
        try
        {
            // Pedir para ver a Ellen durante o silêncio é o mesmo que encerrá-lo.
            if (EllenSilenceService.IsActive) { SetSilence(false); return; }
            if (_instance == null) { EnsureShown(); return; }
            if (_instance.IsVisible) { _instance._userHidden = true; _instance._hideAfterChat = false; _instance.Hide(); }
            else { _instance._userHidden = false; _instance.Show(); }
        }
        catch (Exception ex)
        {
            Logger.LogException("DesktopQuoteWindow.Toggle", ex);
        }
    }

    /// <summary>Modo compacto ligado nas preferências (para o item da bandeja).</summary>
    public static bool IsCompact => _instance?._state.Compact ?? EllenWidgetSettings.Load().Compact;

    /// <summary>Alterna compacto/expandido (item da bandeja).</summary>
    public static void ToggleCompact()
    {
        try { _instance?.SetCompact(!_instance._state.Compact); }
        catch (Exception ex) { Logger.LogException("DesktopQuoteWindow.ToggleCompact", ex); }
    }

    /// <summary>Volta o widget ao canto padrão do monitor principal (item da bandeja).</summary>
    public static void ResetPosition()
    {
        try { _instance?.ResetToDefaultCorner(); }
        catch (Exception ex) { Logger.LogException("DesktopQuoteWindow.ResetPosition", ex); }
    }

    /// <summary>Liga (1 hora) ou desliga o Modo Silêncio. Chamar na thread da interface.</summary>
    public static void SetSilence(bool on)
    {
        try
        {
            if (on) EllenSilenceService.Start(); else EllenSilenceService.Stop();
            Logger.Log(on ? "Modo Silêncio da Ellen ligado por 1 hora." : "Modo Silêncio da Ellen encerrado.");
            if (_instance == null) EnsureShown();
            else _instance.ApplySilence();
        }
        catch (Exception ex)
        {
            Logger.LogException("DesktopQuoteWindow.SetSilence", ex);
        }
    }

    // Falas espontâneas: um relógio sorteia o próximo momento (ver EllenChatterService) e, ao
    // disparar, a Ellen fala por alguns segundos e o cartão volta para a frase do dia.
    private readonly DispatcherTimer _chatTimer = new();
    private readonly DispatcherTimer _revertTimer = new();
    private readonly DispatcherTimer _silenceTimer = new(); // dispara quando a hora de silêncio acaba
    private bool _silenced;
    private bool _userHidden;      // a pessoa fechou o widget no "x" ou pela bandeja
    private bool _showAfterSilence; // o widget deve voltar quando o silêncio acabar
    private bool _firstChat = true;
    private bool _hideAfterChat; // o widget estava fechado: aparece só para a fala e some de novo

    private const double ExpandedCardWidth = 460;
    private const double CardMargin = 14; // = Margin do CardHost no XAML (folga da sombra)

    private readonly EllenWidgetState _state = EllenWidgetSettings.Load();
    private bool _tempExpanded; // compacto, mas expandido só durante uma fala da Ellen
    private readonly DispatcherTimer _layoutTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // arrastar
    private bool _dragArmed, _dragging;
    private POINT _dragStartCursor;
    private int _dragStartX, _dragStartY, _dragW, _dragH, _lastX, _lastY;
    private List<WidgetScreen>? _dragScreens;
    private WidgetScreen? _lastArea;

    private bool VisualCompact => _state.Compact && !_tempExpanded;

    private double Scale
    {
        get
        {
            try { return System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX; }
            catch { return 1.0; }
        }
    }

    private int MarginPx => (int)Math.Round(CardMargin * Scale);

    private DesktopQuoteWindow()
    {
        InitializeComponent();

        _chatTimer.Tick += (_, _) => OnChatTick();
        _revertTimer.Tick += (_, _) => OnRevertTick();
        _silenceTimer.Tick += (_, _) => ApplySilence();
        _layoutTimer.Tick += (_, _) => { _layoutTimer.Stop(); if (!_dragging) Reposition(); };
        ScheduleNextChat();
        Closed += (_, _) => { _chatTimer.Stop(); _revertTimer.Stop(); _silenceTimer.Stop(); _layoutTimer.Stop(); };

        ApplyMode(animate: false);

        SizeChanged += (_, _) => Reposition();
        IsVisibleChanged += (_, _) => { if (IsVisible) Reposition(); };
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; // barra de tarefas movida/redimensionada
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        };
    }

    // Os eventos chegam várias vezes seguidas e de outra thread: reagrupa e reposiciona uma vez só.
    private void OnDisplayChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(RestartLayoutTimer));

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(RestartLayoutTimer));

    private void RestartLayoutTimer() { _layoutTimer.Stop(); _layoutTimer.Start(); }

    // ---------- Posição (monitores) ----------

    private static List<WidgetScreen> ReadScreens()
    {
        var list = new List<WidgetScreen>();
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
        {
            var w = s.WorkingArea;
            list.Add(new WidgetScreen(s.DeviceName, w.Left, w.Top, w.Right, w.Bottom, s.Primary));
        }
        return list;
    }

    private static WidgetScreen? AreaAt(List<WidgetScreen> screens, int x, int y)
    {
        var device = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).DeviceName;
        foreach (var s in screens)
            if (s.Device == device) return s;
        return EllenWidgetSettings.Pick(screens, null);
    }

    private (int W, int H) SizePx => ((int)Math.Round(ActualWidth * Scale), (int)Math.Round(ActualHeight * Scale));

    private void MoveTo(int x, int y)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // Põe a janela onde as preferências mandam (monitor salvo ou principal, preso ao canto salvo).
    // Posiciona em PIXELS com SetWindowPos, como a trava de tela, para não depender de conversão de
    // DPI entre monitores.
    private void Reposition()
    {
        try
        {
            if (_dragging) return;
            var (w, h) = SizePx;
            if (w <= 0 || h <= 0) return;

            var area = EllenWidgetSettings.Pick(ReadScreens(), _state.Placed ? _state.Monitor : null);
            if (area is not { } a) return;

            var (x, y) = EllenWidgetSettings.Resolve(_state, a, w, h, MarginPx, Scale);
            MoveTo(x, y);
        }
        catch (Exception ex)
        {
            Logger.LogException("DesktopQuoteWindow.Reposition", ex);
        }
    }

    private void CommitPosition(WidgetScreen area, int x, int y)
    {
        var (w, h) = SizePx;
        _state.Placed = true;
        _state.Monitor = area.Device;
        EllenWidgetSettings.Capture(_state, area, x, y, w, h, MarginPx, Scale);
        EllenWidgetSettings.Save(_state);
        Reposition();
    }

    private void ResetToDefaultCorner()
    {
        _state.ResetPosition();
        EllenWidgetSettings.Save(_state);
        Reposition();
    }

    private void MoveToMonitor(string device)
    {
        _state.Monitor = device;
        _state.Placed = true; // mantém o canto e as distâncias, só troca de monitor
        EllenWidgetSettings.Save(_state);
        Reposition();
    }

    // ---------- Modo compacto ----------

    private void SetCompact(bool compact)
    {
        _state.Compact = compact;
        _tempExpanded = false;
        EllenWidgetSettings.Save(_state);
        ApplyMode(animate: true);
    }

    private void ApplyMode(bool animate)
    {
        var compact = VisualCompact;
        Card.Compact = compact;
        Card.Width = compact ? double.NaN : ExpandedCardWidth;
        CardHost.CornerRadius = new CornerRadius(compact ? 22 : 14);
        CardHost.Cursor = compact ? System.Windows.Input.Cursors.Hand : null;
        CardHost.ToolTip = compact ? "Ellen — clique para expandir, arraste para mover (botão direito: opções)" : null;

        var buttons = compact ? Visibility.Collapsed : Visibility.Visible;
        HideButton.Visibility = buttons;
        SilenceButton.Visibility = buttons;
        CompactButton.Visibility = buttons;

        UpdateLayout();
        Reposition();
        if (animate && IsVisible) UiMotion.FadeSlideIn(CardHost, 6, 200);
    }

    private void CompactButton_Click(object sender, RoutedEventArgs e) => SetCompact(true);

    // ---------- Arrastar ----------

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetCursorPos(out var cur) || !GetWindowRect(hwnd, out var rc)) return;

            _dragStartCursor = cur;
            _dragStartX = rc.Left;
            _dragStartY = rc.Top;
            _dragW = rc.Right - rc.Left;
            _dragH = rc.Bottom - rc.Top;
            _dragging = false;
            _dragArmed = true;
            Root.CaptureMouse();
        }
        catch (Exception ex)
        {
            Logger.LogException("DesktopQuoteWindow.DragStart", ex);
        }
    }

    private void Root_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragArmed) return;
        try
        {
            if (e.LeftButton != MouseButtonState.Pressed) { EndDragState(); return; }
            if (!GetCursorPos(out var cur)) return;

            var dx = cur.X - _dragStartCursor.X;
            var dy = cur.Y - _dragStartCursor.Y;

            if (!_dragging)
            {
                // Pequena tolerância: um clique com a mão tremendo não vira arrasto.
                if (Math.Abs(dx) < GetSystemMetrics(SM_CXDRAG) && Math.Abs(dy) < GetSystemMetrics(SM_CYDRAG)) return;
                _dragging = true;
                _dragScreens = ReadScreens();
            }

            // O cartão segue o mouse e fica dentro da área útil do monitor onde o mouse está.
            if (AreaAt(_dragScreens!, cur.X, cur.Y) is not { } area) return;
            var (x, y) = EllenWidgetSettings.ClampWindow(area, _dragStartX + dx, _dragStartY + dy, _dragW, _dragH, MarginPx);
            _lastX = x;
            _lastY = y;
            _lastArea = area;
            MoveTo(x, y);
        }
        catch (Exception ex)
        {
            Logger.LogException("DesktopQuoteWindow.DragMove", ex);
            EndDragState();
        }
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragArmed) return;
        var wasDragging = _dragging;
        var area = _lastArea;
        var (x, y) = (_lastX, _lastY);
        EndDragState();

        if (wasDragging)
        {
            if (area is { } a) CommitPosition(a, x, y);
            e.Handled = true;
        }
        else if (VisualCompact)
        {
            SetCompact(false); // clique no avatar compacto = expandir
            e.Handled = true;
        }
    }

    private void Root_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Perdeu o mouse no meio do arrasto (ex.: Alt+Tab): encerra e guarda onde ficou.
        if (_dragging && _lastArea is { } a)
        {
            var (x, y) = (_lastX, _lastY);
            EndDragState();
            CommitPosition(a, x, y);
            return;
        }
        EndDragState();
    }

    private void EndDragState()
    {
        _dragArmed = false;
        _dragging = false;
        _dragScreens = null;
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
    }

    // ---------- Menu do botão direito ----------

    private void Root_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();

            var compact = VisualCompact;
            menu.Items.Add(compact ? "Expandir" : "Compactar (só o avatar)", null, (_, _) => SetCompact(!compact));

            var screens = System.Windows.Forms.Screen.AllScreens;
            if (screens.Length > 1)
            {
                var current = EllenWidgetSettings.Pick(ReadScreens(), _state.Placed ? _state.Monitor : null)?.Device;
                var sub = new System.Windows.Forms.ToolStripMenuItem("Mover para o monitor");
                for (var i = 0; i < screens.Length; i++)
                {
                    var scr = screens[i];
                    var label = $"Monitor {i + 1}{(scr.Primary ? " (principal)" : "")} — {scr.Bounds.Width}×{scr.Bounds.Height}";
                    var device = scr.DeviceName;
                    var item = new System.Windows.Forms.ToolStripMenuItem(label) { Checked = device == current };
                    item.Click += (_, _) => MoveToMonitor(device);
                    sub.DropDownItems.Add(item);
                }
                menu.Items.Add(sub);
            }

            menu.Items.Add("Voltar ao canto padrão", null, (_, _) => ResetToDefaultCorner());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Modo Silêncio (1 hora)", null, (_, _) => SetSilence(true));
            menu.Items.Add("Esconder", null, (_, _) => HideButton_Click(this, new RoutedEventArgs()));

            // Descarta depois: o Closed pode disparar antes do Click do item.
            menu.Closed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(menu.Dispose));
            menu.Show(System.Windows.Forms.Control.MousePosition);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Logger.LogException("DesktopQuoteWindow.ContextMenu", ex);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Logger.LogException("DesktopQuoteWindow.OnSourceInitialized", ex);
        }
        Reposition();
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        _userHidden = true;
        _hideAfterChat = false;
        Hide();
    }

    private void SilenceButton_Click(object sender, RoutedEventArgs e) => SetSilence(true);

    // ---------- Modo Silêncio ----------

    /// <summary>
    /// Põe a janela em dia com o Modo Silêncio: ligado = esconde o widget e para as falas;
    /// desligado (ou vencido) = devolve o widget se ele estava na tela e retoma as falas.
    /// </summary>
    private void ApplySilence()
    {
        try
        {
            var remaining = EllenSilenceService.Remaining;
            if (remaining is { } left)
            {
                if (!_silenced)
                {
                    _silenced = true;
                    _showAfterSilence = !_userHidden;
                    _chatTimer.Stop();
                    _revertTimer.Stop();
                    _hideAfterChat = false;
                    Card.EndInterjection();
                    Hide();
                    if (_tempExpanded) { _tempExpanded = false; ApplyMode(animate: false); }
                }
                _silenceTimer.Stop();
                _silenceTimer.Interval = left + TimeSpan.FromSeconds(1);
                _silenceTimer.Start();
                return;
            }

            _silenceTimer.Stop();
            if (!_silenced) return;

            _silenced = false;
            EllenSilenceService.Stop(); // limpa o arquivo vencido
            if (_showAfterSilence) Show();
            _showAfterSilence = false;
            ScheduleNextChat(); // volta a falar só daqui a alguns minutos, não na hora
        }
        catch (Exception ex)
        {
            Logger.LogException("DesktopQuoteWindow.ApplySilence", ex);
        }
    }

    // ---------- Falas espontâneas ----------

    private void ScheduleNextChat()
    {
        _chatTimer.Stop();
        _chatTimer.Interval = EllenChatterService.NextDelay(_firstChat);
        _firstChat = false;
        _chatTimer.Start();
    }

    private void OnChatTick()
    {
        try
        {
            Chat();
        }
        catch (Exception ex)
        {
            Logger.LogException("DesktopQuoteWindow.OnChatTick", ex);
        }
        finally
        {
            ScheduleNextChat();
        }
    }

    private void Chat()
    {
        // Modo Silêncio: nada de falas (o relógio já fica parado; isto cobre uma virada de tela tardia).
        if (EllenSilenceService.IsActive) { ApplySilence(); return; }

        // Não interrompe durante a trava de tela.
        if (ScreenLock.IsShowing) return;

        var state = StateManager.Load();
        if (state.IsScreenLockActive) return;

        TimeSpan? blockRemaining = state.IsLocked && state.LockedUntilUtc.HasValue
            ? state.LockedUntilUtc.Value - DateTime.UtcNow
            : null;

        var line = EllenChatterService.Pick(DateTime.Now, blockRemaining);
        ShowLine(line);
    }

    /// <summary>
    /// Fala de reação a um evento (ex.: bloqueio cumprido), vinda de fora do widget. Respeita o Modo
    /// Silêncio e a trava de tela (nesses casos não fala). Sem efeito se o widget não existe.
    /// </summary>
    public static void React(EllenQuote line)
    {
        try { _instance?.ShowReaction(line); }
        catch (Exception ex) { Logger.LogException("DesktopQuoteWindow.React", ex); }
    }

    private void ShowReaction(EllenQuote line)
    {
        if (EllenSilenceService.IsActive) return;
        if (ScreenLock.IsShowing || StateManager.Load().IsScreenLockActive) return;

        ShowLine(line);
        ScheduleNextChat(); // não solta uma fala sorteada logo depois da reação
    }

    private void ShowLine(EllenQuote line)
    {
        // Widget fechado (no "x" ou pela bandeja)? Ela reaparece só para falar e depois some de novo.
        if (!IsVisible)
        {
            _hideAfterChat = true;
            Show();
        }

        // Compacto não tem balão: expande só durante a fala (volta em OnRevertTick).
        if (_state.Compact && !_tempExpanded)
        {
            _tempExpanded = true;
            ApplyMode(animate: false);
        }

        Card.ShowInterjection(line);
        UiMotion.FadeSlideIn(CardHost, 14, 320); // chamadinha de atenção: o cartão "sobe" de leve

        // Fica mais tempo na tela se a fala for maior (leitura sem pressa), entre 14 e 28 segundos.
        var seconds = Math.Clamp(8 + line.Text.Length * 0.09, 14, 28);
        _revertTimer.Stop();
        _revertTimer.Interval = TimeSpan.FromSeconds(seconds);
        _revertTimer.Start();
    }

    private void OnRevertTick()
    {
        // Se o mouse está em cima do cartão (a pessoa está lendo ou mexendo), espera mais um pouco.
        if (IsMouseOver)
        {
            _revertTimer.Interval = TimeSpan.FromSeconds(3);
            return;
        }

        _revertTimer.Stop();

        if (_hideAfterChat)
        {
            _hideAfterChat = false;
            Hide();
        }
        Card.EndInterjection();

        if (_tempExpanded)
        {
            _tempExpanded = false;
            ApplyMode(animate: true);
        }
    }
}
