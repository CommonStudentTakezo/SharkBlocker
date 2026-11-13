using System.Collections.Generic;
using System.Linq;

namespace SharkBlocker.Services;

/// <summary>
/// Sorteio em "sacola" (shuffle bag): esgota todas as opções antes de repetir qualquer uma e nunca
/// repete a mesma duas vezes seguidas (nem na virada de uma sacola para outra). Uma sacola por chave.
/// </summary>
public static class ShuffleBag
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Queue<int>> Bags = new();
    private static readonly Dictionary<string, int> Last = new();

    public static T Next<T>(string key, IReadOnlyList<T> items)
    {
        if (items.Count == 0) throw new System.ArgumentException("lista vazia", nameof(items));
        if (items.Count == 1) return items[0];
        lock (Gate)
        {
            if (!Bags.TryGetValue(key, out var queue) || queue.Count == 0)
            {
                var idx = Enumerable.Range(0, items.Count).ToArray();
                for (var i = idx.Length - 1; i > 0; i--)
                {
                    var j = System.Random.Shared.Next(i + 1);
                    (idx[i], idx[j]) = (idx[j], idx[i]);
                }
                if (Last.TryGetValue(key, out var previous) && idx[0] == previous)
                {
                    var j = 1 + System.Random.Shared.Next(idx.Length - 1);
                    (idx[0], idx[j]) = (idx[j], idx[0]);
                }
                queue = new Queue<int>(idx);
                Bags[key] = queue;
            }
            var pick = queue.Dequeue();
            if (pick >= items.Count) pick = System.Random.Shared.Next(items.Count);
            Last[key] = pick;
            return items[pick];
        }
    }
}
