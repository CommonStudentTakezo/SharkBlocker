using System.Windows;
using System.Windows.Input;
using SharkBlocker.Services;
using Button = System.Windows.Controls.Button;

namespace SharkBlocker.Dialogs;

/// <summary>Lista do diário (mais novas primeiro), com resumo, "Registrar agora" e "Apagar" por anotação.</summary>
public partial class JournalWindow : Window
{
    public JournalWindow()
    {
        InitializeComponent();
        Render();
    }

    private void Render()
    {
        var all = RelapseJournal.LoadAll();
        var s = RelapseJournal.Summarize(all, DateTime.UtcNow);

        SummaryText.Text = all.Count == 0
            ? "Ainda não há anotações. Escrever o que aconteceu ajuda a enxergar o padrão (horário, emoção, situação) e a montar um plano."
            : $"{s.Total} anotações · {s.Relapses} recaídas · {s.Last30Days} nos últimos 30 dias" +
              (s.AverageIntensity > 0 ? $" · vontade média {s.AverageIntensity:0.0} de 5" : "");
        InsightText.Text = s.TopTrigger != null && s.TopTriggerCount >= 2
            ? $"Gatilho mais comum: {s.TopTrigger} ({s.TopTriggerCount} vezes). Vale um plano só para ele."
            : "";
        InsightText.Visibility = InsightText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        EntriesPanel.Children.Clear();
        foreach (var entry in all) EntriesPanel.Children.Add(BuildCard(entry));
    }

    private UIElement BuildCard(JournalEntry e)
    {
        var body = new System.Windows.Controls.StackPanel();
        body.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = RelapseJournal.OneLine(e),
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource(e.WasRelapse ? "ColorAccent" : "ColorTextPrimary"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (e.Activity.Length > 0) body.Children.Add(Line("Fazendo", e.Activity));
        if (e.WhatWouldHelp.Length > 0) body.Children.Add(Line("Teria ajudado", e.WhatWouldHelp));

        var delete = new Button
        {
            Content = "Apagar",
            Style = (Style)FindResource("GhostButton"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var id = e.Id;
        delete.Click += (_, _) =>
        {
            var ok = ThemedMessageBox.Show(this,
                "Apagar esta anotação? O texto some, mas a recaída (se foi uma) continua contada nas estatísticas.",
                "Diário", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (ok != MessageBoxResult.Yes) return;
            RelapseJournal.Delete(id);
            Render();
        };
        body.Children.Add(delete);

        return new System.Windows.Controls.Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(14, 12, 14, 10),
            Margin = new Thickness(0, 0, 0, 10),
            Child = body,
        };
    }

    private static System.Windows.Controls.TextBlock Line(string label, string text) => new()
    {
        Text = $"{label}: {text}",
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
        LineHeight = 20,
        Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("ColorTextSecondary"),
    };

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        JournalUi.Open(this, defaultRelapse: true, RelapseJournal.SourceManual);
        Render();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { /* o clique pode terminar antes do arrasto começar */ }
    }
}
