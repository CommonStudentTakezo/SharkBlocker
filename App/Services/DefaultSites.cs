using SharkBlocker.Models;

namespace SharkBlocker.Services;

/// <summary>
/// MIGRAÇÃO ÚNICA (versão 2). Na versão 1 esta classe juntava uma lista de sites à lista da pessoa, inclusive YouTube inteiro.
/// Agora os sites padrão são PERMANENTES e vivem em CTC_PERMANENT_SITES (adult-terms.js; sempre bloqueados, sem depender de
/// bloqueio temporário) e o YouTube só tem os Shorts barrados. Esta migração só remove da lista da pessoa as 5 entradas de
/// YouTube que a versão 1 pode ter acrescentado, para o YouTube deixar de ser bloqueado por inteiro nos bloqueios temporários.
/// </summary>
public static class DefaultSites
{
    public const int Version = 2;

    private static readonly string[] LegacyYouTube =
    {
        "youtube.com", "m.youtube.com", "youtu.be", "youtube-nocookie.com", "youtubeeducation.com",
    };

    public static void EnsureMerged()
    {
        try
        {
            StateManager.RunLocked(() =>
            {
                var state = StateManager.Load();
                if (state.DefaultSitesVersion >= Version) return true;

                var kept = (state.BlockedWebsites ?? new List<string>())
                    .Where(d => !LegacyYouTube.Contains(d?.Trim() ?? "", StringComparer.OrdinalIgnoreCase))
                    .ToList();
                var removed = (state.BlockedWebsites?.Count ?? 0) - kept.Count;

                state.BlockedWebsites = kept; // lista NOVA (ver ShallowCopy)
                state.DefaultSitesVersion = Version;
                StateManager.Save(state);
                Logger.Log($"Migração da lista padrão de sites v{Version}: {removed} entrada(s) de YouTube removida(s) da lista da pessoa.");
                return true;
            });
        }
        catch (Exception ex)
        {
            Logger.LogException("DefaultSites.EnsureMerged", ex);
        }
    }
}
