using System.IO;
using System.Text.Json;
using SharkBlocker.Models;
using SharkBlocker.Services;
using Xunit;

namespace SharkBlocker.ParityTests;

/// <summary>
/// Os MESMOS casos de tests/parity/cases.json que a extensão (referência) e o app Android usam.
/// Só os casos de URL (a avaliação pública). Se um falhar, o C# divergiu do JS: corrija o C#
/// (ou, se a regra mudou de propósito, mude JS, C# e Kotlin juntos e regenere os casos).
/// </summary>
public class ParityTests
{
    [Fact]
    public void CasosDeUrl()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "parity", "cases.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var fails = new List<string>();
        var arr = doc.RootElement.GetProperty("url_cases");
        foreach (var c in arr.EnumerateArray())
        {
            var st = c.GetProperty("state");
            var locked = st.GetProperty("locked").GetBoolean();
            var state = new BlockState
            {
                PermanentProtectionsEnabled = true,
                BlockWebArchives = st.GetProperty("blockWebArchives").GetBoolean(),
                LockedUntilUtc = locked ? DateTime.UtcNow.AddHours(1) : null,
            };
            foreach (var s in st.GetProperty("sites").EnumerateArray()) state.BlockedWebsites.Add(s.GetString()!);
            foreach (var k in st.GetProperty("keywords").EnumerateArray()) state.BlockedKeywords.Add(k.GetString()!);

            var expectedEl = c.GetProperty("expected");
            string? expected = expectedEl.ValueKind == JsonValueKind.Null ? null : expectedEl.GetString();
            var url = c.GetProperty("url").GetString()!;
            var got = UrlPolicy.Evaluate(url, state);
            if (got != expected) fails.Add($"{c.GetProperty("label").GetString()}: {url} esperado={expected ?? "null"} obtido={got ?? "null"}");
        }
        Assert.True(fails.Count == 0, "Divergências do C# em relação ao JS:\n" + string.Join("\n", fails));
    }
}
