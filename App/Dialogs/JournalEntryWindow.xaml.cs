using System.Windows;
using System.Windows.Input;
using SharkBlocker.Services;
using Button = System.Windows.Controls.Button;

namespace SharkBlocker.Dialogs;

/// <summary>
/// Janela de UMA anotação do diário (gatilho, intensidade, o que fazia, o que teria ajudado). Não grava nada: devolve
/// <see cref="Result"/> e quem chamou (<see cref="JournalUi"/>) chama <see cref="RelapseJournal.Add"/>.
/// O tema não tem CheckBox/ComboBox próprios, então as escolhas são botões-chip (estilo ChipButton; selecionado = PrimaryButton).
/// </summary>
public partial class JournalEntryWindow : Window
{
    private readonly string _source;
    private bool _relapse;
    private string _trigger = "";
    private int _intensity;
    private readonly List<Button> _triggerButtons = new();
    private readonly List<Button> _intensityButtons = new();

    /// <summary>null = cancelou.</summary>
    public JournalEntry? Result { get; private set; }

    /// <param name="defaultRelapse">true = abre como "Foi uma recaída" (botão da aba Status); false = "Só quero anotar" (convite após trava).</param>
    public JournalEntryWindow(bool defaultRelapse, string source)
    {
        InitializeComponent();
        _relapse = defaultRelapse;
        _source = source;

        foreach (var trigger in RelapseJournal.Triggers)
        {
            var value = trigger;
            var b = NewChip(value);
            b.Click += (_, _) => { _trigger = _trigger == value ? "" : value; RefreshChips(); };
            TriggerPanel.Children.Add(b);
            _triggerButtons.Add(b);
        }
        for (var i = 1; i <= 5; i++)
        {
            var n = i;
            var b = NewChip(n.ToString());
            b.MinWidth = 44;
            b.Click += (_, _) => { _intensity = _intensity == n ? 0 : n; RefreshChips(); };
            IntensityPanel.Children.Add(b);
            _intensityButtons.Add(b);
        }

        ActivityBox.MaxLength = RelapseJournal.MaxTextLength;
        HelpBox.MaxLength = RelapseJournal.MaxTextLength;
        RefreshChips();

        Loaded += (_, _) => Activate();
    }

    private Button NewChip(string text) => new() { Content = text };

    private void Mark(Button b, bool selected)
    {
        b.Style = (Style)FindResource(selected ? "PrimaryButton" : "ChipButton");
        b.Margin = new Thickness(0, 0, 8, 8);
        b.Padding = new Thickness(14, 6, 14, 6);
        b.FontSize = 12;
    }

    private void RefreshChips()
    {
        Mark(ModeRelapseButton, _relapse);
        Mark(ModeNoteButton, !_relapse);
        foreach (var b in _triggerButtons) Mark(b, (string)b.Content == _trigger);
        for (var i = 0; i < _intensityButtons.Count; i++) Mark(_intensityButtons[i], _intensity == i + 1);

        TitleText.Text = _relapse ? "Diário — registrar recaída" : "Diário — anotar o que aconteceu";
        EffectText.Text = _relapse
            ? "Salvar vai zerar a sequência de dias. O recorde e o histórico continuam. Cair não apaga o que você já construiu."
            : "Só uma anotação: a sequência de dias NÃO é alterada.";
    }

    private void ModeRelapseButton_Click(object sender, RoutedEventArgs e) { _relapse = true; RefreshChips(); }

    private void ModeNoteButton_Click(object sender, RoutedEventArgs e) { _relapse = false; RefreshChips(); }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var entry = new JournalEntry
        {
            Utc = DateTime.UtcNow,
            Trigger = _trigger,
            Activity = ActivityBox.Text,
            Intensity = _intensity,
            WhatWouldHelp = HelpBox.Text,
            WasRelapse = _relapse,
            Source = _source,
        };

        if (!RelapseJournal.IsWorthSaving(RelapseJournal.Normalize(entry)))
        {
            ThemedMessageBox.Show(this, "Escolha pelo menos um item, escreva algo ou marque como recaída para salvar.",
                "Diário", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Result = entry;
        DialogResult = true;
    }

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        // Só o cabeçalho arrasta a janela (os campos de texto e botões ficam livres).
        try { DragMove(); } catch { /* o clique pode terminar antes do arrasto começar */ }
    }
}
