using System.IO;
using System.Text.Json;

namespace SharkBlocker.Services;

/// <summary>
/// Área útil de um monitor (já sem a barra de tarefas), em pixels do mesmo espaço de coordenadas
/// que o Win32/WinForms usam neste processo (o mesmo que a trava de tela usa em
/// <c>Screen.Bounds</c> + <c>SetWindowPos</c>).
/// </summary>
public readonly record struct WidgetScreen(string Device, int Left, int Top, int Right, int Bottom, bool IsPrimary);

/// <summary>
/// Preferências do widget da Ellen (modo compacto e posição). Só enfeite: nada aqui afeta bloqueios.
///
/// A posição NÃO é guardada como "x, y" soltos: fica presa ao canto mais próximo da área útil do
/// monitor (Anchor = TL/TR/BL/BR) com a distância até as bordas em DIPs. Assim, se a resolução ou a
/// escala mudar, ou se a barra de tarefas for movida, o widget continua "colado" no mesmo canto; e
/// alternar entre compacto e expandido cresce para dentro da tela em vez de sair dela.
/// </summary>
public sealed class EllenWidgetState
{
    // Posição padrão (igual à do widget antes de ele ser arrastável): canto inferior esquerdo,
    // ~16 de margem da borda e ~12 acima da barra de tarefas (distâncias do CARTÃO, sem a folga da sombra).
    public const double DefaultOffsetX = 16;
    public const double DefaultOffsetY = 12;

    /// <summary>Modo compacto: só o avatar da Ellen.</summary>
    public bool Compact { get; set; }

    /// <summary>true = a pessoa já arrastou/escolheu um monitor; false = canto padrão do monitor principal.</summary>
    public bool Placed { get; set; }

    /// <summary>Nome do dispositivo do monitor (ex.: \\.\DISPLAY2). Se não existir mais, usa o principal.</summary>
    public string? Monitor { get; set; }

    /// <summary>Canto de ancoragem: TL, TR, BL ou BR.</summary>
    public string Anchor { get; set; } = "BL";

    /// <summary>Distância do cartão até a borda esquerda/direita da área útil, em DIPs.</summary>
    public double OffsetX { get; set; } = DefaultOffsetX;

    /// <summary>Distância do cartão até a borda de cima/baixo da área útil, em DIPs.</summary>
    public double OffsetY { get; set; } = DefaultOffsetY;

    public void ResetPosition()
    {
        Placed = false;
        Monitor = null;
        Anchor = "BL";
        OffsetX = DefaultOffsetX;
        OffsetY = DefaultOffsetY;
    }
}

public static class EllenWidgetSettings
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SharkBlocker", "ellen-widget.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // ---------- Persistência ----------

    /// <summary>Lê as preferências. Arquivo ausente, ilegível ou com valores absurdos = padrão (é só enfeite).</summary>
    public static EllenWidgetState Load()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return new EllenWidgetState();

            var state = JsonSerializer.Deserialize<EllenWidgetState>(AtomicFile.ReadAllText(SettingsFile));
            return Sanitize(state);
        }
        catch
        {
            return new EllenWidgetState();
        }
    }

    public static void Save(EllenWidgetState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            AtomicFile.WriteSmall(SettingsFile, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.LogException("EllenWidgetSettings.Save", ex);
        }
    }

    private static EllenWidgetState Sanitize(EllenWidgetState? s)
    {
        if (s == null) return new EllenWidgetState();

        var anchor = (s.Anchor ?? "").ToUpperInvariant();
        if (anchor is not ("TL" or "TR" or "BL" or "BR")) anchor = "BL";
        s.Anchor = anchor;

        if (!double.IsFinite(s.OffsetX) || s.OffsetX < 0 || s.OffsetX > 5000) s.OffsetX = EllenWidgetState.DefaultOffsetX;
        if (!double.IsFinite(s.OffsetY) || s.OffsetY < 0 || s.OffsetY > 5000) s.OffsetY = EllenWidgetState.DefaultOffsetY;
        if (s.Monitor is { Length: > 64 }) s.Monitor = null;
        return s;
    }

    // ---------- Geometria (pura: sem WPF, sem WinForms) ----------

    /// <summary>
    /// Monitor onde o widget deve ficar: o salvo (se ainda existir) ou o principal. NÃO apaga o monitor
    /// salvo: se ele for desconectado e voltar depois, o widget volta para lá.
    /// </summary>
    public static WidgetScreen? Pick(IReadOnlyList<WidgetScreen> screens, string? device)
    {
        if (screens.Count == 0) return null;

        if (!string.IsNullOrEmpty(device))
        {
            foreach (var s in screens)
                if (string.Equals(s.Device, device, StringComparison.OrdinalIgnoreCase)) return s;
        }

        foreach (var s in screens)
            if (s.IsPrimary) return s;

        return screens[0];
    }

    /// <summary>
    /// Posição da JANELA (x, y em pixels) para o estado salvo, no monitor <paramref name="area"/>.
    /// <paramref name="marginPx"/> é a folga da sombra entre a janela e o cartão; os deslocamentos
    /// salvos valem para o CARTÃO, e o cartão nunca sai da área útil.
    /// </summary>
    public static (int X, int Y) Resolve(EllenWidgetState s, WidgetScreen area, int winW, int winH, int marginPx, double scale)
    {
        var cardW = Math.Max(1, winW - 2 * marginPx);
        var cardH = Math.Max(1, winH - 2 * marginPx);
        var offX = (int)Math.Round(s.OffsetX * scale);
        var offY = (int)Math.Round(s.OffsetY * scale);

        var cardLeft = s.Anchor.EndsWith('L') ? area.Left + offX : area.Right - offX - cardW;
        var cardTop = s.Anchor.StartsWith('T') ? area.Top + offY : area.Bottom - offY - cardH;

        return ClampWindow(area, cardLeft - marginPx, cardTop - marginPx, winW, winH, marginPx);
    }

    /// <summary>Mantém o CARTÃO dentro da área útil (a folga da sombra pode passar da borda).</summary>
    public static (int X, int Y) ClampWindow(WidgetScreen area, int x, int y, int winW, int winH, int marginPx)
    {
        var minX = area.Left - marginPx;
        var maxX = Math.Max(minX, area.Right - winW + marginPx);
        var minY = area.Top - marginPx;
        var maxY = Math.Max(minY, area.Bottom - winH + marginPx);
        return (Math.Clamp(x, minX, maxX), Math.Clamp(y, minY, maxY));
    }

    /// <summary>
    /// Grava no estado o canto mais próximo e a distância até as bordas, a partir de onde a janela
    /// ficou (x, y, largura e altura em pixels).
    /// </summary>
    public static void Capture(EllenWidgetState s, WidgetScreen area, int winX, int winY, int winW, int winH, int marginPx, double scale)
    {
        var cardLeft = winX + marginPx;
        var cardTop = winY + marginPx;
        var cardRight = winX + winW - marginPx;
        var cardBottom = winY + winH - marginPx;

        var onLeft = (cardLeft + cardRight) / 2 < (area.Left + area.Right) / 2;
        var onTop = (cardTop + cardBottom) / 2 < (area.Top + area.Bottom) / 2;

        var offX = onLeft ? cardLeft - area.Left : area.Right - cardRight;
        var offY = onTop ? cardTop - area.Top : area.Bottom - cardBottom;

        s.Anchor = (onTop ? "T" : "B") + (onLeft ? "L" : "R");
        s.OffsetX = Math.Round(Math.Max(0, offX / scale), 1);
        s.OffsetY = Math.Round(Math.Max(0, offY / scale), 1);
    }
}
