using System.IO;
using System.Text.Json;

namespace SharkBlocker.Services;

/// <summary>Uma anotação do diário. Tudo é opcional, menos a data (automática).</summary>
public sealed class JournalEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Utc { get; set; } = DateTime.UtcNow;
    /// <summary>Gatilho/emoção (um item de <see cref="RelapseJournal.Triggers"/> ou texto livre curto).</summary>
    public string Trigger { get; set; } = "";
    /// <summary>O que a pessoa estava fazendo.</summary>
    public string Activity { get; set; } = "";
    /// <summary>Intensidade da vontade, 1 a 5 (0 = não informada).</summary>
    public int Intensity { get; set; }
    /// <summary>O que teria ajudado / plano para a próxima vez.</summary>
    public string WhatWouldHelp { get; set; } = "";
    /// <summary>true = foi uma recaída (zerou a sequência); false = só anotou a vontade.</summary>
    public bool WasRelapse { get; set; }
    /// <summary>"manual" (botão da aba Status) ou "after-lock" (convite depois de uma trava).</summary>
    public string Source { get; set; } = "manual";
}

/// <summary>Números do cabeçalho do diário.</summary>
public sealed record JournalSummary(int Total, int Relapses, int Last30Days, string? TopTrigger, int TopTriggerCount, double AverageIntensity);

/// <summary>
/// DIÁRIO DE RECAÍDAS (só no App; o Watchdog não o compila). Fica em <c>ProgramData\SharkBlocker\diario.json</c>, num arquivo
/// SEPARADO do state.json assinado e do stats.json: é um texto pessoal, não segurança. Nada é enviado, nem vai para o
/// diagnóstico, para a extensão ou para o Android.
///
/// Privacidade (limite honesto): a pasta de dados dá leitura a todos os usuários do PC (só administradores escrevem) e o arquivo
/// não é criptografado. Em um PC pessoal isso basta; em um PC compartilhado, outra conta do Windows poderia ler o arquivo.
///
/// Efeito na sequência: uma entrada com <see cref="JournalEntry.WasRelapse"/> chama <see cref="StatsStore.RecordRelapse"/> (zera a
/// sequência e suja o dia nos "dias limpos"). Uma entrada que só anota a vontade (WasRelapse = false) NÃO mexe na sequência.
/// Apagar uma anotação não desfaz a recaída nas estatísticas (de propósito: apagar o texto não pode "limpar" o dia).
/// </summary>
public static class RelapseJournal
{
    public const int MaxEntries = 2000;
    public const int MaxTextLength = 400;
    public const int MaxTriggerLength = 40;

    public const string SourceManual = "manual";
    public const string SourceAfterLock = "after-lock";

    /// <summary>Opções rápidas de gatilho (a última, "Outro", deixa a pessoa escrever o que quiser em "O que eu estava fazendo").</summary>
    public static readonly string[] Triggers =
    {
        "Tédio", "Estresse", "Solidão", "Cansaço", "Ansiedade", "Tristeza", "Muito tempo livre", "Outro",
    };

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SharkBlocker", "diario.json");

    private static readonly object Gate = new();

    // ---------------------------------------------------------------- regras puras (testáveis)

    /// <summary>Apara espaços, limita tamanhos e valores. Devolve uma CÓPIA; não altera a original.</summary>
    public static JournalEntry Normalize(JournalEntry e) => new()
    {
        Id = e.Id == Guid.Empty ? Guid.NewGuid() : e.Id,
        Utc = e.Utc == default ? DateTime.UtcNow : e.Utc.ToUniversalTime(),
        Trigger = Clip((e.Trigger ?? "").Trim(), MaxTriggerLength),
        Activity = Clip((e.Activity ?? "").Trim(), MaxTextLength),
        Intensity = Math.Clamp(e.Intensity, 0, 5),
        WhatWouldHelp = Clip((e.WhatWouldHelp ?? "").Trim(), MaxTextLength),
        WasRelapse = e.WasRelapse,
        Source = e.Source == SourceAfterLock ? SourceAfterLock : SourceManual,
    };

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Uma anotação vazia (sem gatilho, texto nem intensidade) só vale se for recaída (a data já é a informação).</summary>
    public static bool IsWorthSaving(JournalEntry e) =>
        e.WasRelapse || e.Trigger.Length > 0 || e.Activity.Length > 0 || e.WhatWouldHelp.Length > 0 || e.Intensity > 0;

    public static JournalSummary Summarize(IEnumerable<JournalEntry> entries, DateTime nowUtc)
    {
        var list = entries.ToList();
        var cut = nowUtc.AddDays(-30);
        var top = list
            .Where(e => e.Trigger.Length > 0)
            .GroupBy(e => e.Trigger, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();
        var rated = list.Where(e => e.Intensity > 0).ToList();
        return new JournalSummary(
            Total: list.Count,
            Relapses: list.Count(e => e.WasRelapse),
            Last30Days: list.Count(e => e.Utc >= cut),
            TopTrigger: top?.Key,
            TopTriggerCount: top?.Count() ?? 0,
            AverageIntensity: rated.Count == 0 ? 0 : rated.Average(e => e.Intensity));
    }

    /// <summary>Texto de uma linha para a lista: "14/11 07:52 · Tédio · vontade 4 · recaída".</summary>
    public static string OneLine(JournalEntry e)
    {
        var parts = new List<string> { e.Utc.ToLocalTime().ToString("dd/MM HH:mm") };
        if (e.Trigger.Length > 0) parts.Add(e.Trigger);
        if (e.Intensity > 0) parts.Add($"vontade {e.Intensity}");
        parts.Add(e.WasRelapse ? "recaída" : "só anotou");
        return string.Join(" · ", parts);
    }

    // ---------------------------------------------------------------- armazenamento

    /// <summary>Todas as anotações, da mais nova para a mais antiga. Nunca lança (arquivo ruim = lista vazia + log).</summary>
    public static List<JournalEntry> LoadAll()
    {
        lock (Gate)
        {
            return LoadUnlocked().OrderByDescending(e => e.Utc).ToList();
        }
    }

    /// <summary>
    /// Grava a anotação. Se <see cref="JournalEntry.WasRelapse"/>, zera a sequência (<see cref="StatsStore.RecordRelapse"/>).
    /// Devolve false (e loga) se não deu para gravar; nesse caso a sequência NÃO é zerada.
    /// </summary>
    public static bool Add(JournalEntry entry)
    {
        var e = Normalize(entry);
        if (!IsWorthSaving(e)) return false;

        try
        {
            lock (Gate)
            {
                var all = LoadUnlocked();
                all.Add(e);
                if (all.Count > MaxEntries)
                    all = all.OrderBy(x => x.Utc).Skip(all.Count - MaxEntries).ToList();
                SaveUnlocked(all);
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("RelapseJournal.Add", ex);
            return false;
        }

        // Só depois de gravar com sucesso: o texto nunca se perde por causa da estatística, e a sequência só zera se houve registro.
        if (e.WasRelapse) StatsStore.RecordRelapse();
        Logger.Log($"Diário: nova anotação ({(e.WasRelapse ? "recaída" : "só anotou")}, origem {e.Source}). O texto não vai para o log.");
        return true;
    }

    /// <summary>Apaga uma anotação (não desfaz a recaída nas estatísticas). Devolve true se existia.</summary>
    public static bool Delete(Guid id)
    {
        try
        {
            lock (Gate)
            {
                var all = LoadUnlocked();
                var removed = all.RemoveAll(x => x.Id == id) > 0;
                if (removed) SaveUnlocked(all);
                return removed;
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("RelapseJournal.Delete", ex);
            return false;
        }
    }

    private static List<JournalEntry> LoadUnlocked()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var text = AtomicFile.ReadAllText(FilePath);
            var list = Parse(text);
            if (list != null) return list;

            // Arquivo corrompido: tenta o .bak/instantâneo mais recente antes de desistir.
            if (AtomicFile.TryRecover(FilePath, s => Parse(s) != null, out _))
                return Parse(AtomicFile.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { Logger.LogException("RelapseJournal.Load (lista vazia)", ex); }
        return new();
    }

    /// <summary>null = JSON inválido.</summary>
    private static List<JournalEntry>? Parse(string json)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<JournalEntry>>(json);
            return list?.Select(Normalize).ToList();
        }
        catch (JsonException) { return null; }
    }

    private static void SaveUnlocked(List<JournalEntry> all)
    {
        var json = JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true });
        // Escrita atômica com .bak e instantâneos: é texto que a pessoa escreveu, não pode se perder por um desligamento no meio.
        AtomicFile.WriteAllTextAtomic(FilePath, json, s => Parse(s) != null);
    }
}
