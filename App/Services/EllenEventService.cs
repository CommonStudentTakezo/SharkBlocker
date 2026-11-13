using SharkBlocker.Models;

namespace SharkBlocker.Services;

/// <summary>
/// Falas da Ellen que REAGEM a eventos (em vez de só sair por sorteio de tempo):
///
///  - Busca barrada: <see cref="CaughtLine"/> abre o texto da trava de tela ("Peguei essa."). A fala
///    vai na própria trava porque o widget da área de trabalho fica coberto por ela.
///  - Bloqueio cumprido: <see cref="OnTick"/> percebe que o temporizador de bloqueio chegou ao fim
///    e devolve uma fala de parabéns para o widget. Bloqueio removido ANTES da hora (senha +
///    confirmações) não comemora.
///
/// Só do App (usa EllenQuote). Tom: seco, protetor, sem culpa.
/// </summary>
public static class EllenEventService
{
    // ---------------------------------------------------------------- busca barrada

    private static readonly string[] Caught =
    {
        "Peguei essa.",
        "Peguei essa. Eu estava de olho.",
        "Peguei essa. Sem drama, acontece.",
        "Peguei essa. Agora respira.",
        "Peguei essa. Nem tente fingir que não foi nada.",
        "Hmph! Eu vi isso. Fica aí uns minutos.",
        "Peguei. De novo? Tudo bem, a gente respira junto.",
        "Peguei essa. Eu avisei que ficava de olho. Não que eu esteja feliz com isso.",
        "Disfarçar não funciona comigo. Peguei.",
        "Peguei essa. E não, eu não estou decepcionada. Só... atenta.",
    };

    /// <summary>Primeira linha da trava por busca barrada (sorteio em sacola: não repete antes de esgotar).</summary>
    public static string CaughtLine() => ShuffleBag.Next("caught", Caught);

    // ---------------------------------------------------------------- bloqueio cumprido

    // {0} = duração do bloqueio, quando o app a conhece.
    private static readonly (string Text, string Mood)[] DoneKnownLength =
    {
        ("Cumpriu! {0} de bloqueio, até o fim. Parabéns, de verdade.", "warm"),
        ("Fim do bloqueio: {0} cumpridos. Isso não foi sorte, foi decisão. Parabéns.", "determined"),
        ("{0} de bloqueio e você chegou até o fim. Estou orgulhosa. Não vou repetir.", "smirk"),
        ("{0} de bloqueio cumpridos. Hmph... eu não esperava menos. Quer dizer, esperava. Quer dizer... parabéns.", "shy"),
        ("Fim do bloqueio: {0}. Eu odeio admitir, mas isso foi impressionante.", "warm"),
        ("{0} até o fim. Respira, você merece. Eu vou fingir que não estou sorrindo.", "shy"),
    };

    private static readonly (string Text, string Mood)[] DoneGeneric =
    {
        ("Cumpriu! O bloqueio foi até o fim. Parabéns, de verdade.", "warm"),
        ("Bloqueio encerrado. Isso não foi sorte, foi decisão. Parabéns.", "determined"),
        ("Tempo cumprido. Estou orgulhosa. Não vou repetir.", "smirk"),
        ("Bloqueio cumprido até o fim. Hmph. Eu sabia. Quer dizer, eu esperava. Quer dizer... parabéns.", "shy"),
        ("Terminou. Não é que eu tenha ficado aliviada; é que eu aprecio quem cumpre o combinado.", "smirk"),
        ("Você conseguiu. Eu não duvidei. Tá, duvidei um pouquinho. Mas foi um pouquinho.", "warm"),
    };

    // Bloqueio que o app está acompanhando (o fim previsto) e, se foi iniciado por este app, a duração.
    private static DateTime? _trackedUntil;
    private static TimeSpan? _trackedLength;
    private static TimeSpan? _pendingLength;

    /// <summary>Chamado quando ESTE app inicia um bloqueio: a duração entra na fala de parabéns.</summary>
    public static void NoteBlockStarted(TimeSpan length) => _pendingLength = length;

    /// <summary>
    /// Chamado a cada tick da janela principal. Devolve a fala de parabéns UMA vez, quando um bloqueio
    /// acompanhado chega ao fim por tempo; senão null.
    /// </summary>
    public static EllenQuote? OnTick(BlockState state, DateTime nowUtc)
    {
        try
        {
            if (state.IsLocked && state.LockedUntilUtc is { } until)
            {
                if (_trackedUntil is not { } tracked || Math.Abs((until - tracked).TotalSeconds) > 2)
                {
                    _trackedUntil = until;
                    _trackedLength = _pendingLength;
                    _pendingLength = null;
                }
                return null;
            }

            if (_trackedUntil is { } ended)
            {
                var length = _trackedLength;
                _trackedUntil = null;
                _trackedLength = null;

                // Terminou por tempo (e não removido antes da hora, com senha): comemora.
                if (nowUtc >= ended - TimeSpan.FromSeconds(10))
                    return Completed(length);
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("EllenEventService.OnTick", ex);
        }
        return null;
    }

    private static EllenQuote Completed(TimeSpan? length)
    {
        string text, mood;
        if (length is { } len && len >= TimeSpan.FromMinutes(1))
        {
            var (t, m) = ShuffleBag.Next("doneKnown", DoneKnownLength);
            text = string.Format(t, EllenChatterService.Describe(len));
            mood = m;
        }
        else
        {
            (text, mood) = ShuffleBag.Next("doneGeneric", DoneGeneric);
        }

        // Sequência de dias sem tentar burlar (só quando já passou de 1 dia: dá motivo concreto).
        try
        {
            var s = StatsStore.Snapshot(DateTime.UtcNow);
            if (s.StreakDays >= 1) text += $" Sequência em {StatsStore.FormatStreak(s.StreakDays)}.";
        }
        catch (Exception ex)
        {
            Logger.LogException("EllenEventService.Completed (sequência)", ex);
        }

        return new EllenQuote(text, mood);
    }
}
