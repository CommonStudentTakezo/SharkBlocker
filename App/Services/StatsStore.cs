using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SharkBlocker.Services;

public sealed class StatsEvent
{
    public DateTime Utc { get; set; }
    /// <summary>"search" (busca/endereço barrado), "bypass" (tentativa de burlar) ou "relapse" (recaída declarada no diário).</summary>
    public string Kind { get; set; } = "";
    /// <summary>Motivo original da trava (closed, tamper, uninstall, removal, search). Só diagnóstico.</summary>
    public string Reason { get; set; } = "";
}

public sealed class StatsData
{
    /// <summary>Início da contagem (primeira vez que as estatísticas existiram neste PC).</summary>
    public DateTime FirstSeenUtc { get; set; }
    public DateTime? LastBypassUtc { get; set; }
    public double BestStreakDays { get; set; }
    public double BlockedSecondsTotal { get; set; }
    /// <summary>Segundos de bloqueio por dia local ("yyyy-MM-dd"). Guarda só os últimos dias.</summary>
    public Dictionary<string, double> BlockedSecondsByDay { get; set; } = new();
    public List<StatsEvent> Events { get; set; } = new();
    public DateTime? LastWeeklySummaryUtc { get; set; }
}

public sealed record StatsSummary(
    double StreakDays,
    double BestStreakDays,
    int SearchesWeek,
    int BypassWeek,
    double BlockedSecondsWeek,
    double BlockedSecondsTotal,
    DateTime SinceUtc,
    int CleanDays30 = 0,
    int TotalDays30 = 0,
    int[]? RiskHours = null,
    RiskWindow? RiskSuggestion = null,
    int RelapsesMonth = 0);

/// <summary>Janela de horário (local) com mais ocorrências. <see cref="EndHourExclusive"/> pode ser menor que o início (atravessa a meia-noite).</summary>
public sealed record RiskWindow(int StartHour, int EndHourExclusive, int Covered, int Total);

/// <summary>
/// Sequência e estatísticas motivacionais: dias seguidos sem tentar burlar, buscas barradas nos
/// últimos 7 dias e tempo total com o bloqueio ativo.
///
/// Fica num arquivo separado (stats.json), FORA do state.json assinado: é só motivação, não
/// segurança, então mexer nele não pode afetar senha, trava ou bloqueio. Todo acesso usa o mesmo
/// mutex global do StateManager (App e Watchdog escrevem). Arquivo COMPARTILHADO com o Watchdog:
/// sem WPF aqui.
///
/// O que conta:
///  - "bypass" (zera a sequência): app fechado à força, Watchdog finalizado, tentativa de
///    desinstalar ou de remover itens/desativar proteções.
///  - "search": endereço/busca barrado que gerou trava (adulto, proxy, termo proibido).
///  - Tempo bloqueado: segundos com o temporizador de bloqueio ativo, contados pelo Watchdog.
/// </summary>
public static class StatsStore
{
    public const string KindSearch = "search";
    public const string KindBypass = "bypass";
    /// <summary>Recaída DECLARADA pela própria pessoa no diário: zera a sequência, mas não é "tentativa de burlar".</summary>
    public const string KindRelapse = "relapse";
    public const string ReasonRelapse = "relapse";

    private static readonly string StatsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SharkBlocker", "stats.json");

    // Mesmo incidente reportado por mais de um lado (ex.: Watchdog e Guard) conta uma vez só.
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(60);
    private const int MaxEvents = 500;
    private const int KeepDays = 60;
    private const int FlushEverySeconds = 60;

    private static readonly object TickLock = new();
    private static double _pendingBlockedSeconds;
    private static DateTime? _lastTickUtc;

    /// <summary>Motivo da trava -> tipo de evento (null = não entra nas estatísticas).</summary>
    public static string? KindFor(string? reason) => reason switch
    {
        // Literais de ScreenLock.ReasonSearch/ReasonUninstall/ReasonRemoval (ScreenLock é só do App).
        "search" or "image" => KindSearch,
        AppPresence.ReasonClosed or AppPresence.ReasonTamper or "uninstall" or "removal" => KindBypass,
        ReasonRelapse => KindRelapse,
        _ => null
    };

    // ---------------------------------------------------------------- registro de eventos

    /// <summary>Registra o evento ligado ao motivo da trava. Nunca lança: estatística não pode atrapalhar a trava.</summary>
    public static void Record(string? reason)
    {
        try
        {
            var kind = KindFor(reason);
            if (kind == null) return;
            var now = DateTime.UtcNow;

            StateManager.RunLocked(() =>
            {
                var d = Load(now);
                if (d.Events.Any(e => e.Kind == kind && now >= e.Utc && now - e.Utc < DedupWindow)) return true;

                d.Events.Add(new StatsEvent { Utc = now, Kind = kind, Reason = reason ?? "" });

                if (kind == KindBypass || kind == KindRelapse)
                {
                    var since = d.LastBypassUtc ?? d.FirstSeenUtc;
                    d.BestStreakDays = Math.Max(d.BestStreakDays, Math.Max(0, (now - since).TotalDays));
                    d.LastBypassUtc = now;
                }

                Prune(d, now);
                Save(d);
                return true;
            });
        }
        catch (Exception ex) { Logger.LogException("StatsStore.Record", ex); }
    }

    /// <summary>Recaída declarada no diário (zera a sequência). Nunca lança.</summary>
    public static void RecordRelapse() => Record(ReasonRelapse);

    // ---------------------------------------------------------------- tempo bloqueado (Watchdog)

    /// <summary>Chamado a cada ciclo do Watchdog enquanto há bloqueio ativo. Acumula em memória e grava ~1x por minuto.</summary>
    public static void TickBlocked(DateTime nowUtc)
    {
        try
        {
            double pending;
            lock (TickLock)
            {
                var delta = _lastTickUtc.HasValue ? (nowUtc - _lastTickUtc.Value).TotalSeconds : 0;
                _lastTickUtc = nowUtc;
                // Salto grande (suspensão do PC, relógio mexido, Watchdog reiniciado): não conta.
                if (delta <= 0 || delta > 30) return;
                _pendingBlockedSeconds += delta;
                pending = _pendingBlockedSeconds;
            }
            if (pending >= FlushEverySeconds) FlushBlocked(nowUtc);
        }
        catch (Exception ex) { Logger.LogException("StatsStore.TickBlocked", ex); }
    }

    /// <summary>O bloqueio não está ativo: grava o que sobrou e zera o relógio do próximo trecho.</summary>
    public static void EndBlocked()
    {
        try
        {
            lock (TickLock) { _lastTickUtc = null; }
            FlushBlocked(DateTime.UtcNow);
        }
        catch (Exception ex) { Logger.LogException("StatsStore.EndBlocked", ex); }
    }

    private static void FlushBlocked(DateTime nowUtc)
    {
        double seconds;
        lock (TickLock)
        {
            seconds = _pendingBlockedSeconds;
            _pendingBlockedSeconds = 0;
        }
        if (seconds <= 0) return;

        StateManager.RunLocked(() =>
        {
            var d = Load(nowUtc);
            d.BlockedSecondsTotal += seconds;
            var key = DayKey(nowUtc);
            d.BlockedSecondsByDay[key] = (d.BlockedSecondsByDay.TryGetValue(key, out var cur) ? cur : 0) + seconds;
            Prune(d, nowUtc);
            Save(d);
            return true;
        });
    }

    // ---------------------------------------------------------------- leitura

    /// <summary>Resumo para a aba Status. Cria o arquivo na primeira chamada (fixa o início da contagem).</summary>
    public static StatsSummary Snapshot(DateTime nowUtc) =>
        StateManager.RunLocked(() => Summarize(Load(nowUtc, ensureFile: true), nowUtc));

    /// <summary>
    /// Fala semanal da Ellen: devolve o texto (e o humor do avatar) no máximo 1x a cada 7 dias,
    /// contados desde o início da contagem ou da última fala. Fora disso devolve null.
    /// </summary>
    public static (string Text, string Mood)? TryTakeWeeklySummary(DateTime nowUtc)
    {
        try
        {
            return StateManager.RunLocked<(string, string)?>(() =>
            {
                var d = Load(nowUtc, ensureFile: true);
                var last = d.LastWeeklySummaryUtc ?? d.FirstSeenUtc;
                if (nowUtc - last < TimeSpan.FromDays(7)) return null;

                d.LastWeeklySummaryUtc = nowUtc;
                Save(d);
                return WeeklyText(Summarize(d, nowUtc));
            });
        }
        catch (Exception ex)
        {
            Logger.LogException("StatsStore.TryTakeWeeklySummary", ex);
            return null;
        }
    }

    private static StatsSummary Summarize(StatsData d, DateTime nowUtc)
    {
        var since = d.LastBypassUtc ?? d.FirstSeenUtc;
        var streak = Math.Max(0, (nowUtc - since).TotalDays);
        var best = Math.Max(d.BestStreakDays, streak);

        var cut = nowUtc.AddDays(-7);
        var searches = d.Events.Count(e => e.Kind == KindSearch && e.Utc >= cut);
        var bypass = d.Events.Count(e => e.Kind == KindBypass && e.Utc >= cut);

        var firstDay = nowUtc.ToLocalTime().Date.AddDays(-6);
        double blockedWeek = 0;
        foreach (var (key, seconds) in d.BlockedSecondsByDay)
        {
            if (DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day >= firstDay)
                blockedWeek += seconds;
        }

        // Dia "sujo" = tentativa de burlar OU recaída declarada.
        var (clean, total) = CleanDays(
            d.Events.Where(e => e.Kind == KindBypass || e.Kind == KindRelapse).Select(e => e.Utc), d.FirstSeenUtc, nowUtc);

        var hours = HourHistogram(d.Events.Where(e => e.Kind == KindSearch || e.Kind == KindBypass || e.Kind == KindRelapse).Select(e => e.Utc));
        var relapsesMonth = d.Events.Count(e => e.Kind == KindRelapse && e.Utc >= nowUtc.AddDays(-30));

        return new StatsSummary(streak, best, searches, bypass, blockedWeek, d.BlockedSecondsTotal, d.FirstSeenUtc, clean, total,
            hours, SuggestWindow(hours), relapsesMonth);
    }

    // ---------------------------------------------------------------- mapa de horários de risco (backlog #6)

    /// <summary>Quantas ocorrências (buscas barradas e tentativas de burlar) em cada hora LOCAL do dia (0 a 23).</summary>
    public static int[] HourHistogram(IEnumerable<DateTime> eventsUtc)
    {
        var hist = new int[24];
        foreach (var utc in eventsUtc) hist[utc.ToLocalTime().Hour]++;
        return hist;
    }

    /// <summary>
    /// Sugere uma janela de bloqueio a partir do histograma por hora. Regras (simples de explicar):
    ///  - menos de <paramref name="minEvents"/> ocorrências: não sugere (pouco dado vira palpite);
    ///  - parte da melhor janela circular de 3 h e a estende, até 6 h, para um vizinho que tenha pelo menos 1/3 da
    ///    hora mais forte; tira horas vazias das pontas (mínimo de 2 h);
    ///  - só sugere se a janela cobrir pelo menos 40% das ocorrências (senão não há horário claro).
    /// </summary>
    public static RiskWindow? SuggestWindow(int[] hist, int minEvents = 5)
    {
        if (hist == null || hist.Length != 24) return null;
        var total = hist.Sum();
        if (total < minEvents) return null;

        var best = -1;
        var start = 0;
        for (var s = 0; s < 24; s++)
        {
            var c = hist[s] + hist[(s + 1) % 24] + hist[(s + 2) % 24];
            if (c > best) { best = c; start = s; }
        }

        var len = 3;
        var peak = Math.Max(hist[start], Math.Max(hist[(start + 1) % 24], hist[(start + 2) % 24]));
        var threshold = Math.Max(1, (peak + 2) / 3); // teto de peak/3
        while (len < 6)
        {
            var before = hist[(start + 23) % 24];
            var after = hist[(start + len) % 24];
            if (Math.Max(before, after) < threshold) break;
            if (before >= after) start = (start + 23) % 24;
            len++;
        }

        while (len > 2 && hist[start] == 0) { start = (start + 1) % 24; len--; }
        while (len > 2 && hist[(start + len - 1) % 24] == 0) len--;

        var covered = 0;
        for (var i = 0; i < len; i++) covered += hist[(start + i) % 24];
        if (covered * 100 < total * 40) return null;

        return new RiskWindow(start, (start + len) % 24, covered, total);
    }

    /// <summary>Duas linhas para fonte monoespaçada: barras por hora (0h a 23h) e a régua com 0, 6, 12 e 18.</summary>
    public static (string Bars, string Axis) FormatRiskMap(int[] hist)
    {
        const string levels = "·▁▂▃▄▅▆▇█";
        var max = hist.Length == 24 ? hist.Max() : 0;
        var bars = new StringBuilder();
        for (var h = 0; h < 24; h++)
        {
            var count = hist.Length == 24 ? hist[h] : 0;
            var level = count == 0 || max == 0 ? 0 : Math.Min(8, Math.Max(1, (int)Math.Ceiling(8.0 * count / max)));
            bars.Append(levels[level]);
        }

        var axis = new char[26];
        Array.Fill(axis, ' ');
        axis[0] = '0';
        axis[6] = '6';
        axis[12] = '1'; axis[13] = '2';
        axis[18] = '1'; axis[19] = '8';
        return (bars.ToString(), new string(axis).TrimEnd());
    }

    /// <summary>
    /// Dias "limpos" na janela dos últimos <paramref name="window"/> dias (padrão 30), contando o dia de hoje:
    /// dia limpo = dia LOCAL sem nenhuma tentativa de burlar. Quem começou há pouco só conta os dias desde o
    /// início (não ganha nem perde dias que não existiam). Tropeçar zera a sequência ATUAL, mas aqui só
    /// "suja" aquele dia: o resto da janela continua valendo, para não ser "tudo ou nada".
    /// </summary>
    public static (int Clean, int Total) CleanDays(
        IEnumerable<DateTime> bypassUtc, DateTime firstSeenUtc, DateTime nowUtc, int window = 30)
    {
        var today = nowUtc.ToLocalTime().Date;
        var start = today.AddDays(-(window - 1));
        var first = firstSeenUtc.ToLocalTime().Date;
        if (first > start) start = first;
        if (start > today) start = today;

        var total = (int)(today - start).TotalDays + 1;
        var dirty = new HashSet<DateTime>();
        foreach (var utc in bypassUtc)
        {
            var day = utc.ToLocalTime().Date;
            if (day >= start && day <= today) dirty.Add(day);
        }
        return (total - dirty.Count, total);
    }

    private static (string Text, string Mood) WeeklyText(StatsSummary s)
    {
        var streak = FormatStreak(s.StreakDays);
        var blocked = FormatDuration(s.BlockedSecondsWeek);
        var searches = s.SearchesWeek == 1 ? "1 busca barrada" : $"{s.SearchesWeek} buscas barradas";

        if (s.BypassWeek == 0 && s.SearchesWeek == 0)
            return ($"Resumo da semana: {streak} sem tentar me enganar, nenhuma busca barrada e {blocked} de bloqueio ativo. " +
                    "Hmph. Tubarão satisfeito... só um pouco.", "shy");

        if (s.BypassWeek == 0)
            return ($"Resumo da semana: {searches}, mas nenhuma tentativa de burlar. Sequência em {streak}, " +
                    $"com {blocked} de bloqueio ativo. Continua assim. ...Não que eu esteja pedindo.", "warm");

        var tries = s.BypassWeek == 1 ? "1 tentativa de burlar" : $"{s.BypassWeek} tentativas de burlar";
        return ($"Resumo da semana: {tries} e {searches}. Hmph. Tropeçar acontece, e eu não vou ficar dando bronca. A sequência recomeçou e já está em {streak}; " +
                $"seu recorde é {FormatStreak(s.BestStreakDays)}" +
                (s.TotalDays30 > 0 ? $", e {s.CleanDays30} dos últimos {s.TotalDays30} dias foram limpos. Isso conta, viu" : "") +
                ". Bora bater. Estou de olho, viu?", "determined");
    }

    // ---------------------------------------------------------------- formatação (UI e falas)

    /// <summary>"7 dias", "1 dia", "5 h" ou "menos de 1 h".</summary>
    public static string FormatStreak(double days)
    {
        if (days >= 1)
        {
            var n = (int)Math.Floor(days);
            return n == 1 ? "1 dia" : $"{n} dias";
        }
        var hours = (int)Math.Floor(days * 24);
        return hours >= 1 ? $"{hours} h" : "menos de 1 h";
    }

    /// <summary>"45 min", "3 h 10 min", "2 d 4 h".</summary>
    public static string FormatDuration(double seconds)
    {
        var minutes = (int)Math.Floor(seconds / 60);
        if (minutes < 1) return "0 min";
        if (minutes < 60) return $"{minutes} min";
        var hours = minutes / 60;
        if (hours < 48) return minutes % 60 == 0 ? $"{hours} h" : $"{hours} h {minutes % 60} min";
        var d = hours / 24;
        return hours % 24 == 0 ? $"{d} d" : $"{d} d {hours % 24} h";
    }

    // ---------------------------------------------------------------- arquivo

    private static string DayKey(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static StatsData Load(DateTime nowUtc, bool ensureFile = false)
    {
        StatsData? d = null;
        try
        {
            if (File.Exists(StatsFile))
                d = JsonSerializer.Deserialize<StatsData>(AtomicFile.ReadAllText(StatsFile));
        }
        catch (Exception ex) { Logger.LogException("StatsStore.Load (recomeçando as estatísticas)", ex); }

        if (d != null)
        {
            d.Events ??= new();
            d.BlockedSecondsByDay ??= new();
            if (d.FirstSeenUtc == default) d.FirstSeenUtc = nowUtc;
            return d;
        }

        d = new StatsData { FirstSeenUtc = nowUtc };
        if (ensureFile) Save(d);
        return d;
    }

    private static void Save(StatsData d)
    {
        var json = JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true });
        AtomicFile.WriteSmall(StatsFile, json);
    }

    private static void Prune(StatsData d, DateTime nowUtc)
    {
        var cut = nowUtc.AddDays(-KeepDays);
        d.Events.RemoveAll(e => e.Utc < cut);
        if (d.Events.Count > MaxEvents) d.Events.RemoveRange(0, d.Events.Count - MaxEvents);

        var cutDay = nowUtc.ToLocalTime().Date.AddDays(-KeepDays);
        foreach (var key in d.BlockedSecondsByDay.Keys.ToList())
        {
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                || day < cutDay)
                d.BlockedSecondsByDay.Remove(key);
        }
    }
}
