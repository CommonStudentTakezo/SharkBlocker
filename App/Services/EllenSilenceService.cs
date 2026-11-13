using System.IO;

namespace SharkBlocker.Services;

/// <summary>
/// Modo Silêncio da Ellen: durante 1 hora o widget da tela fica escondido e ela não puxa assunto
/// (ver <see cref="EllenChatterService"/>). Só afeta o enfeite da Ellen — bloqueios, trava de tela e
/// proteções continuam exatamente iguais.
///
/// O horário de término fica num arquivinho em ProgramData\SharkBlocker: se o app for reaberto no
/// meio da hora (boot, atualização), o silêncio continua só pelo tempo que faltava.
/// </summary>
public static class EllenSilenceService
{
    public static readonly TimeSpan Duration = TimeSpan.FromHours(1);

    private static readonly string SilenceFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SharkBlocker", "ellen-silence.txt");

    /// <summary>Quanto falta de silêncio (null = modo desligado).</summary>
    public static TimeSpan? Remaining
    {
        get
        {
            var until = ReadUntilUtc();
            if (until is null) return null;
            var left = until.Value - DateTime.UtcNow;
            return left > TimeSpan.Zero ? left : null;
        }
    }

    public static bool IsActive => Remaining.HasValue;

    /// <summary>Liga o silêncio por 1 hora a partir de agora.</summary>
    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SilenceFile)!);
            AtomicFile.WriteSmall(SilenceFile, (DateTime.UtcNow + Duration).Ticks.ToString());
        }
        catch (Exception ex)
        {
            Logger.LogException("EllenSilenceService.Start", ex);
        }
    }

    /// <summary>Desliga o silêncio (a Ellen volta ao normal).</summary>
    public static void Stop()
    {
        try
        {
            if (File.Exists(SilenceFile)) File.Delete(SilenceFile);
        }
        catch (Exception ex)
        {
            Logger.LogException("EllenSilenceService.Stop", ex);
        }
    }

    private static DateTime? ReadUntilUtc()
    {
        try
        {
            if (!File.Exists(SilenceFile)) return null;
            if (!long.TryParse(AtomicFile.ReadAllText(SilenceFile).Trim(), out var ticks)) return null;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return null;

            var until = new DateTime(ticks, DateTimeKind.Utc);
            // Arquivo com data absurda (relógio mexido, edição à mão): nunca vale mais que a duração.
            return until > DateTime.UtcNow + Duration + TimeSpan.FromMinutes(1) ? null : until;
        }
        catch
        {
            return null; // ilegível = sem silêncio; é só enfeite
        }
    }
}
