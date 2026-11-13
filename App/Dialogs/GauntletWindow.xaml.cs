using System.Windows;
using System.Windows.Input;

namespace SharkBlocker.Dialogs;

/// <summary>
/// Janela ÚNICA da sequência de confirmações. Antes eram 100 caixas de mensagem uma atrás da
/// outra (100 janelas abrindo e fechando, sem noção de quanto faltava). Agora é uma janela só,
/// com contador e barra de progresso, e o botão padrão é "Não, desistir" (Enter/Esc cancelam).
/// A ideia do atrito de propósito continua a mesma: cada "Sim" é uma pergunta diferente.
/// </summary>
public partial class GauntletWindow : Window
{
    private readonly IReadOnlyList<string> _questions;
    private int _index;

    /// <summary>true só se a pessoa respondeu "Sim" a TODAS as perguntas.</summary>
    public bool Completed { get; private set; }

    public GauntletWindow(string actionLabel, IReadOnlyList<string> questions)
    {
        InitializeComponent();
        _questions = questions;
        TitleText.Text = $"Confirmação final — {actionLabel}";
        Progress.Maximum = questions.Count;
        ShowCurrent();

        Loaded += (_, _) =>
        {
            Activate();
            NoButton.Focus();
        };
    }

    private void ShowCurrent()
    {
        CounterText.Text = $"Confirmação {_index + 1} de {_questions.Count}";
        PercentText.Text = $"{(int)(100.0 * _index / _questions.Count)}%";
        Progress.Value = _index;
        QuestionText.Text = _questions[_index];
    }

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        _index++;
        if (_index >= _questions.Count)
        {
            Completed = true;
            DialogResult = true;
            return;
        }

        ShowCurrent();
        // O foco volta pro "Não" a cada pergunta: apertar Enter sem ler cancela, não confirma.
        NoButton.Focus();
    }

    private void No_Click(object sender, RoutedEventArgs e)
    {
        Completed = false;
        DialogResult = false;
    }

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { /* o clique pode terminar antes do arrasto começar */ }
    }
}
