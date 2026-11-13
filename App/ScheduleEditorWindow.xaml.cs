using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SharkBlocker.Models;
using SharkBlocker.Services;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = SharkBlocker.Dialogs.ThemedMessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace SharkBlocker;

/// <summary>
/// Editor da agenda de bloqueios: uma linha por horário (ativo, dias em que COMEÇA, início e fim).
/// As linhas são montadas em código (lista dinâmica). Devolve a agenda em <see cref="Result"/>.
/// Não grava nada: quem chama grava com BlockScheduler.SaveSchedules.
/// </summary>
public partial class ScheduleEditorWindow : Window
{
    private static readonly DayOfWeek[] MondayFirst =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    };

    private static readonly string[] DayLabel = { "Dom", "Seg", "Ter", "Qua", "Qui", "Sex", "Sáb" };

    private sealed class Row
    {
        public string Id = "";
        public Border Root = null!;
        public CheckBox Enabled = null!;
        public CheckBox[] Days = Array.Empty<CheckBox>();
        public TextBox Start = null!;
        public TextBox End = null!;
    }

    private readonly List<Row> _rows = new();

    public List<BlockSchedule> Result { get; private set; } = new();

    public ScheduleEditorWindow(IEnumerable<BlockSchedule>? current)
    {
        InitializeComponent();

        IntroText.Text =
            "Cada horário liga o bloqueio sozinho, toda semana, usando as listas da aba Bloqueio. " +
            "Os dias marcados são os dias em que o bloqueio COMEÇA. Se o fim for menor que o início, " +
            "ele atravessa a meia-noite (ex.: 22:00 às 06:00). Use o formato HH:mm.";

        foreach (var s in current ?? Array.Empty<BlockSchedule>()) AddRow(s);
        if (_rows.Count == 0) AddRow(new BlockSchedule()); // sugestão: todo dia, 22:00 às 06:00
    }

    private Brush Primary => (Brush)FindResource("ColorTextPrimary");
    private Brush Secondary => (Brush)FindResource("ColorTextSecondary");

    private void AddRow(BlockSchedule model)
    {
        var row = new Row { Id = model.Id };

        row.Root = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 10) };
        var panel = new StackPanel();

        // Linha de cima: "Ativo" à esquerda, "Remover" à direita.
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
        var remove = new Button { Content = "Remover", Padding = new Thickness(12, 4, 12, 4) };
        DockPanel.SetDock(remove, Dock.Right);
        remove.Click += (_, _) =>
        {
            RowsHost.Children.Remove(row.Root);
            _rows.Remove(row);
        };
        row.Enabled = new CheckBox
        {
            Content = "Ativo",
            IsChecked = model.Enabled,
            Foreground = Primary,
            VerticalAlignment = VerticalAlignment.Center
        };
        top.Children.Add(remove);
        top.Children.Add(row.Enabled);
        panel.Children.Add(top);

        // Dias da semana (segunda primeiro).
        var days = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        row.Days = MondayFirst.Select(d => new CheckBox
        {
            Content = DayLabel[(int)d],
            Tag = d,
            IsChecked = model.Days?.Contains(d) == true,
            Foreground = Primary,
            Margin = new Thickness(0, 0, 14, 4)
        }).ToArray();
        foreach (var box in row.Days) days.Children.Add(box);
        panel.Children.Add(days);

        // Horários.
        var times = new StackPanel { Orientation = Orientation.Horizontal };
        times.Children.Add(new TextBlock { Text = "Das", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
        row.Start = new TextBox { Text = model.StartTime.ToString(@"hh\:mm"), Width = 76, Margin = new Thickness(8, 0, 8, 0) };
        times.Children.Add(row.Start);
        times.Children.Add(new TextBlock { Text = "às", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center });
        row.End = new TextBox { Text = model.EndTime.ToString(@"hh\:mm"), Width = 76, Margin = new Thickness(8, 0, 0, 0) };
        times.Children.Add(row.End);
        panel.Children.Add(times);

        row.Root.Child = panel;
        RowsHost.Children.Add(row.Root);
        _rows.Add(row);
    }

    private static bool TryParseTime(string? text, out TimeSpan value) =>
        TimeSpan.TryParseExact((text ?? "").Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out value)
        && value >= TimeSpan.Zero && value < TimeSpan.FromHours(24);

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count >= BlockScheduler.MaxSchedules)
        {
            MessageBox.Show($"O máximo é {BlockScheduler.MaxSchedules} horários.", "Limite",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AddRow(new BlockSchedule());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var result = new List<BlockSchedule>();
        var number = 0;
        foreach (var row in _rows)
        {
            number++;
            if (!TryParseTime(row.Start.Text, out var start) || !TryParseTime(row.End.Text, out var end))
            {
                MessageBox.Show($"Horário {number}: use o formato HH:mm, por exemplo 22:00 e 06:00.", "Horário inválido",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var days = row.Days.Where(c => c.IsChecked == true).Select(c => (DayOfWeek)c.Tag).ToList();
            if (days.Count == 0)
            {
                MessageBox.Show($"Horário {number}: marque pelo menos um dia da semana.", "Sem dias",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (start == end)
            {
                MessageBox.Show($"Horário {number}: o início e o fim não podem ser iguais.", "Horário inválido",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            result.Add(new BlockSchedule
            {
                Id = row.Id,
                Enabled = row.Enabled.IsChecked == true,
                Days = days,
                StartTime = start,
                EndTime = end
            });
        }

        Result = result;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        // Cliques em campos e botões não arrastam a janela.
        if (e.OriginalSource is DependencyObject source && IsInteractive(source)) return;
        try { DragMove(); } catch { /* o clique pode terminar antes do arrasto começar */ }
    }

    private static bool IsInteractive(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is TextBox or CheckBox or Button) return true;
            d = (d is Visual or System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }
}
