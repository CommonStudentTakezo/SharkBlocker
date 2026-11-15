using System.Windows;
using SharkBlocker.Services;

namespace SharkBlocker.Dialogs;

/// <summary>Ponto único para abrir o diário: nova anotação, lista e o convite depois de uma trava.</summary>
public static class JournalUi
{
    /// <summary>Abre a janela de nova anotação e grava. Devolve true se algo foi salvo (a sequência pode ter mudado).</summary>
    public static bool Open(Window? owner, bool defaultRelapse, string source)
    {
        var win = new JournalEntryWindow(defaultRelapse, source);
        var ownerUsable = owner != null && owner.IsVisible && owner.IsLoaded && owner.WindowState != WindowState.Minimized;
        if (ownerUsable) win.Owner = owner;
        else
        {
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.Topmost = true;
            win.ShowInTaskbar = true;
        }

        if (win.ShowDialog() != true || win.Result == null) return false;

        var entry = win.Result;
        if (!RelapseJournal.Add(entry))
        {
            ThemedMessageBox.Show(owner, "Não consegui salvar a anotação (detalhes no log). Nada foi alterado, nem a sequência.",
                "Diário", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        ThemedMessageBox.Show(owner,
            entry.WasRelapse
                ? "Anotado. A sequência recomeça hoje, mas o seu recorde e o histórico continuam, e os dias limpos dos últimos 30 dias ainda contam. " +
                  "O mais importante agora é a próxima decisão, não a queda."
                : "Anotado. A sequência continua como está.",
            "Diário", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    public static void ShowList(Window? owner)
    {
        var win = new JournalWindow();
        var ownerUsable = owner != null && owner.IsVisible && owner.IsLoaded && owner.WindowState != WindowState.Minimized;
        if (ownerUsable) win.Owner = owner;
        else
        {
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.Topmost = true;
            win.ShowInTaskbar = true;
        }
        win.ShowDialog();
    }

    /// <summary>
    /// Convite depois que uma trava por busca/imagem acaba: "Quer anotar o que aconteceu?". Pode ser dispensado (nada é gravado).
    /// Só para os motivos que são exposição (busca/imagem); não convida em trava de agenda, urgência, desinstalação etc.
    /// </summary>
    public static bool ShouldInviteAfterLock(string? reason) => reason is ScreenLock.ReasonSearch or ScreenLock.ReasonImage;

    public static void InviteAfterLock(Window? owner)
    {
        var answer = ThemedMessageBox.Show(owner,
            "A trava acabou. Quer anotar o que aconteceu? Leva um minuto, fica só neste PC e ajuda a descobrir o que puxou a vontade.",
            "Diário", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) Open(owner, defaultRelapse: false, RelapseJournal.SourceAfterLock);
    }
}
