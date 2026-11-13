using System.Globalization;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SharkBlocker.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace SharkBlocker.Controls;

/// <summary>
/// Card "Frase do Dia" da Ellen. Mostra a frase do momento (ver <see cref="EllenQuoteService"/>),
/// troca sozinho à meia-noite e tem o botão "Outra frase" para quem quiser mais uma.
/// Tudo aqui é enfeite: qualquer falha (imagem ausente, animação) é engolida e o texto continua
/// aparecendo.
/// </summary>
public partial class EllenQuoteCard : UserControl
{
    private const string FallbackAsset = "lockscreen-mascot.png";

    // Imagens já carregadas (por expressão). null = não existe, usa a de reserva.
    private static readonly Dictionary<string, BitmapImage?> ImageCache = new();

    private readonly DispatcherTimer _typeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _dayTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    private EllenQuote _current = EllenQuoteService.Current();
    private DateOnly _shownDay = DateOnly.FromDateTime(DateTime.Now);
    private bool _firstShowDone;
    private bool _manualPick;

    // Fala espontânea (ver EllenChatterService): mostra uma fala avulsa por um tempo e depois volta
    // para a frase que estava na tela.
    private bool _interjecting;
    private EllenQuote? _restoreQuote;
    private bool _restoreManual;
    private string _typingTarget = "";
    private int _typed;

    public EllenQuoteCard()
    {
        InitializeComponent();

        _typeTimer.Tick += TypeTimer_Tick;
        _dayTimer.Tick += (_, _) => CheckNewDay();

        // Abas descarregam/recarregam o conteúdo ao trocar: Loaded/Unloaded disparam várias vezes.
        Loaded += (_, _) =>
        {
            _dayTimer.Start();

            if (!_firstShowDone)
            {
                _firstShowDone = true;
                ShowQuote(_current, animate: true);
                StartIdleBob();
            }
            else
            {
                CheckNewDay();
                ShowQuote(_current, animate: false); // ao voltar para a aba, já aparece completa
            }
        };

        Unloaded += (_, _) =>
        {
            _typeTimer.Stop();
            _dayTimer.Stop();
        };
    }

    private bool _compact;

    /// <summary>
    /// Modo compacto (widget da tela): esconde o balão e deixa só o avatar da Ellen. A expressão e o
    /// balançar continuam funcionando; o texto segue sendo atualizado por baixo, então ao expandir a
    /// frase já está lá.
    /// </summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;

            BubbleHost.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            CardRoot.Padding = value ? new Thickness(7) : new Thickness(12, 10, 12, 10);
            CardRoot.CornerRadius = new CornerRadius(value ? 22 : 14);
        }
    }

    // Mostra uma frase: texto, expressão e data. Com animate, digita aos poucos.
    private void ShowQuote(EllenQuote quote, bool animate)
    {
        _current = quote;

        OverlineText.Text = _interjecting ? "ELLEN DIZ" : _manualPick ? "FRASE EXTRA" : "FRASE DO DIA";
        DateText.Text = _interjecting ? "" : DateTime.Now.ToString("d 'de' MMMM", new CultureInfo("pt-BR"));
        QuoteSizer.Text = quote.Text;

        SetAvatar(quote.Mood, animate);

        _typeTimer.Stop();
        _typingTarget = quote.Text;

        if (!animate || !UiMotion.Enabled)
        {
            QuoteText.Text = quote.Text;
            return;
        }

        _typed = 0;
        QuoteText.Text = string.Empty;
        _typeTimer.Start();
    }

    private void TypeTimer_Tick(object? sender, EventArgs e)
    {
        // 2 letras por passo (~125 letras/s): rápido o bastante para não irritar quem abre o app todo dia.
        _typed = Math.Min(_typed + 2, _typingTarget.Length);
        QuoteText.Text = _typingTarget[.._typed];

        if (_typed >= _typingTarget.Length)
            _typeTimer.Stop();
    }

    /// <summary>Mostra uma fala espontânea da Ellen (rótulo "ELLEN DIZ"); guarda a frase atual para voltar depois.</summary>
    public void ShowInterjection(EllenQuote quote)
    {
        if (!_interjecting)
        {
            _restoreQuote = _current;
            _restoreManual = _manualPick;
        }
        _interjecting = true;
        ShowQuote(quote, animate: true);
    }

    /// <summary>Termina a fala espontânea e volta para a frase que estava na tela. Sem efeito se não há fala em curso.</summary>
    public void EndInterjection()
    {
        if (!_interjecting) return;
        _interjecting = false;
        _manualPick = _restoreManual;
        ShowQuote(_restoreQuote ?? EllenQuoteService.Current(), animate: true);
    }

    private void AnotherButton_Click(object sender, RoutedEventArgs e)
    {
        _interjecting = false; // pediu outra frase: a fala espontânea acaba aqui
        _manualPick = true;
        ShowQuote(EllenQuoteService.Another(_current.Text), animate: true);
    }

    // Virou o dia com o app aberto (ele fica na bandeja por dias): volta para a frase do novo dia.
    private void CheckNewDay()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _shownDay) return;

        _shownDay = today;
        _interjecting = false;
        _manualPick = false;
        ShowQuote(EllenQuoteService.Current(), animate: IsLoaded);
    }

    // ---------- Expressão da Ellen ----------

    private void SetAvatar(string mood, bool crossfade)
    {
        try
        {
            AvatarBrush.ImageSource = PickVariant(mood) ?? LoadImage(null);
            if (crossfade) UiMotion.FadeIn(AvatarFace, 260);
        }
        catch (Exception ex)
        {
            Logger.LogException("EllenQuoteCard.SetAvatar", ex);
        }
    }

    // Cada expressão pode ter várias imagens: ellen-{mood}.png, ellen-{mood}-2.png, -3, ... (até 6).
    // A cada fala sorteia uma delas, evitando repetir a mesma em seguida. Se não houver nenhuma,
    // cai na imagem de reserva (lockscreen-mascot.png).
    private const int MaxVariants = 6;
    private static readonly Dictionary<string, List<BitmapImage>> VariantCache = new();
    private static BitmapImage? _lastAvatar;

    private static BitmapImage? PickVariant(string mood)
    {
        if (!VariantCache.TryGetValue(mood, out var list))
        {
            list = new List<BitmapImage>();
            for (var i = 1; i <= MaxVariants; i++)
            {
                var file = i == 1 ? $"ellen-{mood}.png" : $"ellen-{mood}-{i}.png";
                var image = LoadAsset(file);
                if (image != null) list.Add(image);
            }
            VariantCache[mood] = list;
        }

        if (list.Count == 0) return null;
        if (list.Count == 1) return _lastAvatar = list[0];

        BitmapImage pick;
        do { pick = list[Random.Shared.Next(list.Count)]; }
        while (ReferenceEquals(pick, _lastAvatar));
        return _lastAvatar = pick;
    }

    private static BitmapImage? LoadAsset(string file)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri($"pack://application:,,,/Assets/{file}", UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null; // arquivo não existe: normal
        }
    }

    // Procura Assets\ellen-{mood}.png; se não existir (ainda), usa lockscreen-mascot.png.
    private static BitmapImage? LoadImage(string? mood)
    {
        var key = mood ?? "_fallback";
        if (ImageCache.TryGetValue(key, out var cached)) return cached;

        var file = mood == null ? FallbackAsset : $"ellen-{mood}.png";
        BitmapImage? image = null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri($"pack://application:,,,/Assets/{file}", UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            image = bmp;
        }
        catch
        {
            // Arquivo ainda não existe no projeto: normal enquanto as novas expressões não chegam.
        }

        ImageCache[key] = image;
        return image;
    }

    // Balanço bem leve (a Ellen "respira"), só se as animações do Windows estiverem ligadas.
    private void StartIdleBob()
    {
        if (!UiMotion.Enabled) return;

        try
        {
            AvatarBob.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(0, -3, TimeSpan.FromSeconds(2.2))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
        }
        catch (Exception ex)
        {
            Logger.LogException("EllenQuoteCard.StartIdleBob", ex);
        }
    }
}
