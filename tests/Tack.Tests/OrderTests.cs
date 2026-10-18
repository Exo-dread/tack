using Tack.Model;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>
/// Tasks with equal order keys (two devices adding at the top at once, or a task back from focus
/// elsewhere with the key it had). Moves must still land where the user puts them.
/// </summary>
internal static class OrderTests
{
    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("order keys: an item put among equal keys lands where it was put (randomised)", KeysAtAmongTies);
        yield return ("Alt+Up or a drag between two tasks with the same key puts the task there", MoveBetweenTied);
        yield return ("the previous focus task takes the new one's place, even between tied tasks", SetNowBetweenTied);
    }

    private static void KeysAtAmongTies()
    {
        long seed = 13;
        int Next(int n) => (int)((seed = seed * 16807 % 2147483647) / 2147483647.0 * n);
        string[] pool = ["0z", "R", "S", "T", "T", "T1", "U", "V", "zz"];
        for (int round = 0; round < 2000; round++)
        {
            var keys = Enumerable.Range(0, Next(8)).Select(_ => pool[Next(pool.Length)]).OrderBy(k => k, StringComparer.Ordinal).ToList();
            int index = Next(keys.Count + 1);
            var placed = OrderKey.KeysAt(keys, index);
            var after = keys.Take(index).Concat(placed).Concat(keys.Skip(index + placed.Length - 1)).ToList();
            string what = $"[{string.Join(",", keys)}] at {index}";

            Check.Equal(keys.Count + 1, after.Count, $"{what}: one key more");
            Check.Sequence(keys.Take(index).ToList(), after.Take(index).ToList(), $"{what}: keys above are kept");
            foreach (var k in placed) Check.True(OrderKey.IsOrderKey(k), $"{what}: {k} is a key");
            for (int i = 1; i < after.Count; i++) Check.True(!OrderKey.Less(after[i], after[i - 1]), $"{what}: still sorted at {i}");
            // Strictly between its neighbours, so it sorts at the index whatever its id.
            if (index > 0) Check.True(OrderKey.Less(after[index - 1], after[index]), $"{what}: after the one above");
            if (index < keys.Count) Check.True(OrderKey.Less(after[index], after[index + 1]), $"{what}: before the one below");
            // Only the neighbours that had no room (the same key as the one above) get new keys.
            int ties = index > 0 ? keys.Skip(index).Count(k => k == keys[index - 1]) : 0;
            Check.Equal(1 + ties, placed.Length, $"{what}: keys given");
        }
    }

    private static void MoveBetweenTied()
    {
        var ws = WithKeys(null, ("a", "S"), ("b", "T"), ("d", "T"), ("c", "U"));
        Check.Equal("a,b,d,c", Texts(ws), "b and d share a key; the id decides");
        ws.MoveOpen(3, 2); // Alt+Up on c (what did nothing in the click-through of 2026-10-18)
        Check.Equal("a,b,c,d", Texts(ws), "c lands between b and d");
        Check.Equal("S,T,U,V", string.Join(",", ws.Open.Select(t => t.Order)), "c and the d below it get new keys; a and b keep theirs");
        Check.Equal(Texts(ws), Texts(Reload(ws)), "the keys say the same");
        ws.MoveOpen(0, 1);
        Check.Equal("b,a,c,d", Texts(ws), "and the next move works as usual");
    }

    private static void SetNowBetweenTied()
    {
        var ws = WithKeys("p", ("p", "Q"), ("e", "R"), ("f", "R"), ("g", "R"), ("a", "S"));
        Check.Equal("e,f,g,a", Texts(ws), "three tasks share a key");
        ws.SetNow(ws.Open[1]);
        Check.Equal("f", ws.Now?.Text, "f is in focus");
        Check.Equal("e,p,g,a", Texts(ws), "p takes f's place, between e and g");
        Check.Equal(Texts(ws), Texts(Reload(ws)), "the keys say the same");
    }

    /// <summary>A workspace holding tasks from elsewhere with the given keys (each id is its text, repeated).</summary>
    private static Workspace WithKeys(string? focus, params (string Text, string Order)[] tasks)
    {
        string Id(string text) => string.Concat(Enumerable.Repeat(text, 12));
        string c = Stamp.Encode(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), 0, "other");
        var data = new WorkspaceData(
            tasks.Select(t => new TaskData(Id(t.Text), t.Text, "2026-10-17T08:00:00.000Z", null, t.Order, false, TaskClocks.All(c))).ToList(),
            [],
            new FocusData(focus == null ? null : Id(focus), c));
        return new Workspace(new Settings(), new Hlc("desk"), data);
    }

    private static Workspace Reload(Workspace ws) => new(new Settings(), new Hlc("desk"), ws.ToData());

    private static string Texts(Workspace ws) => string.Join(",", ws.Open.Select(t => t.Text));
}
