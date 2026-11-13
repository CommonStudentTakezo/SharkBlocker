using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace SharkBlocker.Dialogs;

/// <summary>
/// Substituto "no tema Ellen Joe" para o MessageBox do Windows, com a MESMA forma de uso
/// (<c>Show(texto, título, botões, ícone[, padrão])</c> e a variante com janela dona).
/// Os arquivos do app trocam só a linha <c>using MessageBox = ...</c> e o resto do código
/// continua igual. Pode ser chamado de qualquer thread (despacha para a thread da interface).
/// </summary>
public static class ThemedMessageBox
{
    public static MessageBoxResult Show(
        string text, string caption = "",
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
        => Show(null, text, caption, button, icon, defaultResult);

    public static MessageBoxResult Show(
        Window? owner, string text, string caption = "",
        MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var app = System.Windows.Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            return app.Dispatcher.Invoke(() => Show(owner, text, caption, button, icon, defaultResult));
        }

        var window = new ThemedMessageBoxWindow(text, caption, button, icon, defaultResult);

        // Só usa a janela dona se ela está de fato na tela. Uma janela escondida na bandeja,
        // minimizada ou já fechada não pode ser dona (o diálogo ficaria invisível ou daria erro).
        var ownerUsable = owner != null && owner.IsVisible && owner.IsLoaded &&
                          owner.WindowState != WindowState.Minimized;
        if (ownerUsable)
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Topmost = true;
            window.ShowInTaskbar = true;
        }

        window.ShowDialog();
        return window.Result;
    }
}

public partial class ThemedMessageBoxWindow : Window
{
    private readonly MessageBoxResult _cancelResult;
    private Button? _defaultButton;

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public ThemedMessageBoxWindow(string text, string caption, MessageBoxButton button,
                                  MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        InitializeComponent();

        CaptionText.Text = string.IsNullOrWhiteSpace(caption) ? "Ellen Joe" : caption;
        MessageText.Text = text;
        ApplyIcon(icon);

        switch (button)
        {
            case MessageBoxButton.OKCancel:
                _cancelResult = MessageBoxResult.Cancel;
                AddButton("OK", MessageBoxResult.OK, true, false, defaultResult);
                AddButton("Cancelar", MessageBoxResult.Cancel, false, true, defaultResult);
                break;
            case MessageBoxButton.YesNo:
                _cancelResult = MessageBoxResult.No;
                AddButton("Sim", MessageBoxResult.Yes, true, false, defaultResult);
                AddButton("Não", MessageBoxResult.No, false, true, defaultResult);
                break;
            case MessageBoxButton.YesNoCancel:
                _cancelResult = MessageBoxResult.Cancel;
                AddButton("Sim", MessageBoxResult.Yes, true, false, defaultResult);
                AddButton("Não", MessageBoxResult.No, false, false, defaultResult);
                AddButton("Cancelar", MessageBoxResult.Cancel, false, true, defaultResult);
                break;
            default:
                _cancelResult = MessageBoxResult.OK;
                AddButton("OK", MessageBoxResult.OK, true, true, defaultResult);
                break;
        }

        Loaded += (_, _) =>
        {
            try { (_defaultButton ?? ButtonsPanel.Children.OfType<Button>().FirstOrDefault())?.Focus(); }
            catch { /* foco é só conveniência */ }
        };

        // Alt+F4 / fechar de outro jeito = a resposta "segura" (Não / Cancelar / OK).
        Closing += (_, _) =>
        {
            if (Result == MessageBoxResult.None) Result = _cancelResult;
        };
    }

    private void AddButton(string label, MessageBoxResult result, bool normallyDefault,
                           bool isCancel, MessageBoxResult requestedDefault)
    {
        // Se quem chamou pediu um padrão explícito (ex.: "Não"), ele manda; senão vale o botão
        // que seria o padrão do Windows (o primeiro: OK / Sim).
        var isDefault = requestedDefault != MessageBoxResult.None
            ? requestedDefault == result
            : normallyDefault;

        var b = new Button
        {
            Content = label,
            MinWidth = 96,
            Margin = new Thickness(10, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel,
            Style = (Style)FindResource(isDefault ? "PrimaryButton" : "SecondaryButton")
        };
        b.Click += (_, _) =>
        {
            Result = result;
            Close();
        };
        ButtonsPanel.Children.Add(b);
        if (isDefault) _defaultButton = b;
    }

    private void ApplyIcon(MessageBoxImage icon)
    {
        string glyph;
        string brushKey;
        switch (icon)
        {
            case MessageBoxImage.Error:
                glyph = "✕"; brushKey = "ColorDanger"; break;
            case MessageBoxImage.Warning:
                glyph = "!"; brushKey = "ColorWarning"; break;
            case MessageBoxImage.Question:
                glyph = "?"; brushKey = "ColorAccent"; break;
            default:
                glyph = "i"; brushKey = "ColorIce"; break;
        }

        var color = ((SolidColorBrush)FindResource(brushKey)).Color;
        IconText.Text = glyph;
        IconText.Foreground = new SolidColorBrush(color);
        IconBadge.Background = new SolidColorBrush(Color.FromArgb(48, color.R, color.G, color.B));
        IconBadge.BorderBrush = new SolidColorBrush(color);
        IconBadge.BorderThickness = new Thickness(1.5);
    }

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { /* o clique pode terminar antes do arrasto começar */ }
    }
}
