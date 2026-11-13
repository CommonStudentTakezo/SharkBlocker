using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SharkBlocker.Services;

/// <summary>
/// Animações leves reutilizadas pela interface. Respeita a configuração do Windows
/// "Mostrar animações" (Acessibilidade > Efeitos visuais): se estiver desligada, nada se mexe,
/// o conteúdo simplesmente aparece.
/// </summary>
public static class UiMotion
{
    /// <summary>false quando a pessoa desligou as animações no Windows.</summary>
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Aparece com fade e sobe levemente de <paramref name="fromY"/> até a posição final.</summary>
    public static void FadeSlideIn(UIElement element, double fromY = 10, int durationMs = 240, int delayMs = 0)
    {
        if (element == null) return;

        if (!Enabled)
        {
            element.Opacity = 1;
            return;
        }

        try
        {
            if (element.RenderTransform is not TranslateTransform translate || translate.IsFrozen)
            {
                translate = new TranslateTransform();
                element.RenderTransform = translate;
            }

            // Com atraso, o elemento começa invisível (senão ele "pisca" inteiro antes de a animação começar).
            if (delayMs > 0) element.Opacity = 0;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var begin = TimeSpan.FromMilliseconds(delayMs);
            var duration = TimeSpan.FromMilliseconds(durationMs);

            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, duration) { BeginTime = begin, EasingFunction = ease });
            translate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(fromY, 0, duration) { BeginTime = begin, EasingFunction = ease });
        }
        catch (Exception ex)
        {
            // Animação é enfeite: nunca pode derrubar a tela.
            Logger.LogException("UiMotion.FadeSlideIn", ex);
            element.Opacity = 1;
        }
    }

    /// <summary>Fade simples (usado para trocar a expressão da Ellen).</summary>
    public static void FadeIn(UIElement element, int durationMs = 220)
    {
        if (element == null) return;
        if (!Enabled)
        {
            element.Opacity = 1;
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }
}

/// <summary>
/// Propriedade anexada que deixa o estilo de botão escolher o raio dos cantos
/// (botão normal = 9, "chip" = pílula) sem duplicar o template inteiro.
/// </summary>
public static class ButtonChrome
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "CornerRadius", typeof(CornerRadius), typeof(ButtonChrome),
            new FrameworkPropertyMetadata(new CornerRadius(9)));

    public static CornerRadius GetCornerRadius(DependencyObject obj) =>
        (CornerRadius)obj.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject obj, CornerRadius value) =>
        obj.SetValue(CornerRadiusProperty, value);
}
