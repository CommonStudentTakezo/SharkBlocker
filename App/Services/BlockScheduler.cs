using SharkBlocker.Models;

namespace SharkBlocker.Services;

/// <summary>
/// Bloqueios agendados e recorrentes (ex.: todo dia das 22h às 6h), sem precisar iniciar à mão.
///
/// Como funciona: dentro de uma janela, <see cref="EnsureActive"/> grava o fim da janela em
/// LockedUntilUtc (o mesmo bloqueio temporizado do início manual: assinado, espelhado no Registro,
/// aplicado pelo Watchdog e pelo app). Não há modo novo de bloqueio: o agendamento só "aperta o botão"
/// na hora certa. Janelas encadeadas (uma começa quando a outra termina) viram um bloqueio contínuo.
///
/// Quem chama: o Watchdog a cada ciclo (5 s) e o app a cada ~10 s (reserva, caso o serviço esteja
/// fora). Idempotente e atômico (mesmo mutex global do StateManager).
///
/// Arquivo COMPARTILHADO com o Watchdog: sem WPF aqui.
/// Mudar a agenda depois da primeira vez exige trava de tela de 30 min + senha
/// (UninstallFlow.TryAuthorizeScheduleChange); a primeira configuração é livre.
/// </summary>
public static class BlockScheduler
{
    public const int MaxSchedules = 10;

    private static readonly TimeSpan MaxChain = TimeSpan.FromHours(48);

    private static readonly DayOfWeek[] MondayFirst =
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    };

    private static readonly string[] DayShort = { "dom", "seg", "ter", "qua", "qui", "sex", "sáb" };

    // ---------------------------------------------------------------- validação e cálculo

    public static bool IsValid(BlockSchedule s) =>
        s.Days is { Count: > 0 } &&
        s.StartTime >= TimeSpan.Zero && s.StartTime < TimeSpan.FromHours(24) &&
        s.EndTime >= TimeSpan.Zero && s.EndTime < TimeSpan.FromHours(24) &&
        s.StartTime != s.EndTime;

    private static TimeSpan DurationOf(BlockSchedule s)
    {
        var d = s.EndTime - s.StartTime;
        if (d <= TimeSpan.Zero) d += TimeSpan.FromHours(24); // atravessa a meia-noite
        return d;
    }

    /// <summary>Fim (hora local) da janela que contém <paramref name="instantLocal"/>, ou null se nenhuma contém.</summary>
    private static DateTime? EndOfWindowContaining(IReadOnlyList<BlockSchedule> list, DateTime instantLocal)
    {
        DateTime? best = null;
        foreach (var s in list)
        {
            if (!s.Enabled || !IsValid(s)) continue;
            var dur = DurationOf(s);

            // A janela dura menos de 24 h: ela começou hoje ou ontem.
            for (var offset = 0; offset >= -1; offset--)
            {
                var date = instantLocal.Date.AddDays(offset);
                if (!s.Days.Contains(date.DayOfWeek)) continue;

                var start = date + s.StartTime;
                var end = start + dur;
                if (start <= instantLocal && instantLocal < end && (best == null || end > best))
                    best = end;
            }
        }
        return best;
    }

    /// <summary>
    /// Se agora está dentro de alguma janela, devolve (em UTC) o fim do bloqueio contínuo — somando
    /// janelas que começam exatamente quando a anterior termina, até no máximo 48 h. Senão, null.
    /// </summary>
    public static DateTime? ActiveUntilUtc(IReadOnlyList<BlockSchedule>? list, DateTime nowLocal)
    {
        if (list == null || list.Count == 0) return null;

        DateTime? until = null;
        var cursor = nowLocal;
        for (var i = 0; i < 10; i++)
        {
            var end = EndOfWindowContaining(list, cursor);
            if (end == null || (until != null && end <= until)) break;

            until = end;
            cursor = end.Value;
            if (until.Value - nowLocal > MaxChain) break;
        }

        return until == null
            ? null
            : DateTime.SpecifyKind(until.Value, DateTimeKind.Local).ToUniversalTime();
    }

    /// <summary>Próximo início de janela depois de <paramref name="nowLocal"/> (hora local), nos próximos 8 dias.</summary>
    public static DateTime? NextStartLocal(IReadOnlyList<BlockSchedule>? list, DateTime nowLocal)
    {
        if (list == null) return null;
        DateTime? next = null;
        foreach (var s in list)
        {
            if (!s.Enabled || !IsValid(s)) continue;
            for (var offset = 0; offset <= 8; offset++)
            {
                var date = nowLocal.Date.AddDays(offset);
                if (!s.Days.Contains(date.DayOfWeek)) continue;
                var start = date + s.StartTime;
                if (start > nowLocal && (next == null || start < next)) next = start;
            }
        }
        return next;
    }

    // ---------------------------------------------------------------- ativação e gravação

    /// <summary>
    /// Liga o bloqueio se agora está dentro de uma janela (ou estende um bloqueio que acabaria antes do fim
    /// da janela). Devolve true se gravou algo. Nunca lança.
    /// </summary>
    public static bool EnsureActive()
    {
        try
        {
            return StateManager.RunLocked(() =>
            {
                var state = StateManager.Load();
                var until = ActiveUntilUtc(state.Schedules, DateTime.Now);
                if (until == null) return false;

                // Já bloqueado até o fim da janela (ou além): nada a fazer.
                if (state.IsLocked && state.LockedUntilUtc!.Value >= until.Value.AddSeconds(-1)) return false;

                state.LockedUntilUtc = until;
                StateManager.Save(state);
                Logger.Log($"Bloqueio agendado ativo — bloqueando até {until.Value.ToLocalTime():dd/MM HH:mm}.");
                return true;
            });
        }
        catch (Exception ex)
        {
            Logger.LogException("BlockScheduler.EnsureActive", ex);
            return false;
        }
    }

    /// <summary>Grava a agenda (atômico) e já liga o bloqueio se agora estiver dentro de alguma janela.</summary>
    public static void SaveSchedules(IEnumerable<BlockSchedule> schedules)
    {
        var list = schedules.Take(MaxSchedules).ToList();
        StateManager.RunLocked(() =>
        {
            var state = StateManager.Load();
            state.Schedules = list;
            state.SchedulesConfigured = state.SchedulesConfigured || list.Count > 0;
            StateManager.Save(state);
            return true;
        });
        EnsureActive();
    }

    // ---------------------------------------------------------------- textos da interface

    /// <summary>Ex.: "Todos os dias, 22:00 às 06:00" ou "seg, ter, qua, 18:00 às 20:30 (desativado)".</summary>
    public static string Describe(BlockSchedule s)
    {
        var days = s.Days ?? new List<DayOfWeek>();
        string dayText;
        if (days.Distinct().Count() == 7) dayText = "Todos os dias";
        else if (days.Distinct().Count() == 5 &&
                 new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
                     .All(days.Contains))
            dayText = "Seg a Sex";
        else if (days.Distinct().Count() == 2 && days.Contains(DayOfWeek.Saturday) && days.Contains(DayOfWeek.Sunday))
            dayText = "Sáb e Dom";
        else
            dayText = days.Count == 0
                ? "Nenhum dia"
                : string.Join(", ", MondayFirst.Where(days.Contains).Select(d => DayShort[(int)d]));

        var text = $"{dayText}, {s.StartTime:hh\\:mm} às {s.EndTime:hh\\:mm}";
        if (!IsValid(s)) text += " (inválido)";
        else if (!s.Enabled) text += " (desativado)";
        return text;
    }

    /// <summary>Linha de status: bloqueio agendado ativo, próximo início, ou nada agendado.</summary>
    public static string StatusText(BlockState state, DateTime nowLocal)
    {
        var schedules = state.Schedules;
        if (schedules == null || !schedules.Any(s => s.Enabled && IsValid(s)))
            return "Nenhum bloqueio agendado ativo.";

        var until = ActiveUntilUtc(schedules, nowLocal);
        if (until != null)
        {
            var local = until.Value.ToLocalTime();
            var when = local.Date == nowLocal.Date ? local.ToString("HH:mm") : local.ToString("dd/MM HH:mm");
            return $"Bloqueio agendado em andamento, até {when}.";
        }

        var next = NextStartLocal(schedules, nowLocal);
        if (next == null) return "Nenhum bloqueio agendado nos próximos dias.";

        var day = next.Value.Date == nowLocal.Date ? "hoje"
                : next.Value.Date == nowLocal.Date.AddDays(1) ? "amanhã"
                : $"{DayShort[(int)next.Value.DayOfWeek]}, {next.Value:dd/MM}";
        return $"Próximo bloqueio agendado: {day} às {next.Value:HH:mm}.";
    }
}
