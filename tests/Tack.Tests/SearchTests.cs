using System.IO;
using System.Text.Json;
using Tack.Model;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>
/// Search's matching rule. The cases in tests/shared/search.json are Tack Focus's too
/// (web/tests/unit/search.test.ts): the two apps find exactly the same things.
/// </summary>
internal static class SearchTests
{
    private static readonly long Start = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static JsonElement Shared() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Program.Root, "tests", "shared", "search.json"))).RootElement;

    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("search, shared cases: every fold as Tack Focus folds it", FoldCases);
        yield return ("search, shared cases: every match, and where it is in the text as typed", MatchCases);
        yield return ("search, shared cases: every space searched, in the lists' own order", WorkspaceCases);
        yield return ("search: a note's CR LF line breaks (the TextBox's) match as one, and map back to the TextBox's text", CrLfNotes);
        yield return ("search: a lone surrogate stays as it is, and matches", LoneSurrogates);
        yield return ("search: a match deep in a long note maps back to where it was typed", DeepMatch);
        yield return ("search: what changed since the last query is found, what was deleted forgotten", FollowsEdits);
        yield return ("search: quick on 10,000 tasks and 500 long notes", Quick);
    }

    private static void FoldCases()
    {
        int n = 0;
        foreach (var c in Shared().GetProperty("fold").EnumerateArray())
        {
            string text = c[0].GetString()!, folded = c[1].GetString()!;
            Check.Equal(folded, Search.Fold(text), $"fold {JsonSerializer.Serialize(text)}");
            n++;
        }
        Check.True(n > 20, $"the cases were read ({n})");
    }

    private static void MatchCases()
    {
        foreach (var c in Shared().GetProperty("match").EnumerateArray())
        {
            string text = c.GetProperty("text").GetString()!, query = c.GetProperty("query").GetString()!;
            var range = c.GetProperty("range");
            (int, int)? expected = range.ValueKind == JsonValueKind.Null ? null : (range[0].GetInt32(), range[1].GetInt32());
            Check.Equal(expected, Search.FindIn(text, query), $"find {JsonSerializer.Serialize(query)} in {JsonSerializer.Serialize(text)}");
        }
    }

    /// <summary>The shared workspace, made as the cases say (the same steps as Tack Focus's test).</summary>
    internal static Workspace SharedWorkspace(JsonElement search)
    {
        long wall = Start;
        var ws = new Workspace(new Settings(), new Hlc("search01", () => wall), WorkspaceData.Empty, () => DateTimeOffset.FromUnixTimeMilliseconds(wall));
        var ids = new Dictionary<string, string>();
        foreach (var name in search.GetProperty("spaces").EnumerateArray()) ids[name.GetString()!] = ws.AddSpace(name.GetString()!).Id;
        string? SpaceOf(JsonElement e) => e.TryGetProperty("space", out var s) ? ids[s.GetString()!] : null;
        bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var f) && f.GetBoolean();

        var tasks = search.GetProperty("tasks").EnumerateArray().ToList();
        var made = new TaskItem[tasks.Count];
        for (int i = tasks.Count - 1; i >= 0; i--) made[i] = ws.AddTask(tasks[i].GetProperty("text").GetString()!, Flag(tasks[i], "now"), SpaceOf(tasks[i]));
        for (int i = tasks.Count - 1; i >= 0; i--)
        {
            if (!Flag(tasks[i], "done")) continue;
            wall += 60_000;
            ws.Complete(made[i]);
        }
        foreach (var s in search.GetProperty("sheets").EnumerateArray()) ws.AddSheet(SpaceOf(s), s.GetProperty("text").GetString()!);
        return ws;
    }

    private static List<string> Texts(IEnumerable<SearchHit> hits) => hits.Select(h => JsText.NoteText(h.Text)).ToList();

    private static List<string> Strings(JsonElement list) => list.EnumerateArray().Select(e => e.GetString()!).ToList();

    private static void WorkspaceCases()
    {
        var search = Shared().GetProperty("search");
        var ws = SharedWorkspace(search);
        var tasks = search.GetProperty("tasks").EnumerateArray().ToList();
        Check.Sequence(tasks.Where(t => !t.TryGetProperty("done", out _)).Select(t => t.GetProperty("text").GetString()!).ToList(),
            new[] { ws.Now! }.Concat(ws.Open).Select(t => t.Text).ToList(), "the workspace: open, as the cases say");
        Check.Sequence(tasks.Where(t => t.TryGetProperty("done", out _)).Select(t => t.GetProperty("text").GetString()!).ToList(),
            ws.Done.Select(t => t.Text).ToList(), "and done");

        foreach (var q in search.GetProperty("queries").EnumerateArray())
        {
            string query = q.GetProperty("query").GetString()!;
            var r = new SearchIndex().Find(ws, query);
            string what = JsonSerializer.Serialize(query);
            Check.Sequence(Strings(q.GetProperty("tasks")), Texts(r.Tasks), $"{what}: tasks");
            Check.Sequence(Strings(q.GetProperty("done")), Texts(r.Done), $"{what}: done");
            Check.Sequence(Strings(q.GetProperty("notes")), Texts(r.Notes), $"{what}: notes");
        }
    }

    private static void CrLfNotes()
    {
        // As a note's TextBox has it.
        const string text = "Coordination meeting\r\n- level 3 ducts\r\nvs beams";
        Check.Equal("coordination meeting - level 3 ducts vs beams", Search.Fold(text), "one space per line break");
        var range = Search.FindIn(text, "ducts vs");
        Check.True(range != null, "found");
        Check.Equal("ducts\r\nvs", text[range!.Value.Start..range.Value.End], "the TextBox's own text selected");
        var ws = new Workspace(new Settings(), new Hlc("search02"), WorkspaceData.Empty);
        ws.AddSheet(null, "Plan\n- call the client");
        var hit = new SearchIndex().Find(ws, "plan - call").Notes.Single();
        Check.Equal("Plan\r\n- call the client", hit.Text, "a sheet keeps the TextBox's line breaks");
    }

    private static void LoneSurrogates()
    {
        Check.Equal("a\ud83db", Search.Fold("a\ud83dB"), "a lone high surrogate");
        Check.Equal("\ude00", Search.Fold("\ude00"), "a lone low surrogate");
        Check.Equal(((int, int)?)(1, 2), Search.FindIn("x\ud83d y", "\ud83d"), "found");
        Check.Equal("￿￾", Search.Fold("￿￾"), "noncharacters as they are");
    }

    private static void DeepMatch()
    {
        string text = string.Concat(Enumerable.Repeat("Notes about the façade.\r\n", 20_000)) + "Ünique line";
        string q = Search.FoldQuery("unique");
        int at = Search.Fold(text).IndexOf(q, StringComparison.Ordinal);
        var (s, e) = Search.OriginalRange(text, at, at + q.Length);
        Check.Equal("Ünique", text[s..e], "the word as typed");
    }

    private static void FollowsEdits()
    {
        var ws = new Workspace(new Settings(), new Hlc("search03"), WorkspaceData.Empty);
        var index = new SearchIndex();
        var t = ws.AddTask("Draft the report");
        Check.True(index.Find(ws, "report").Tasks.Single().Item == t, "found");
        t.Text = "Draft the memo";
        Check.Equal(0, index.Find(ws, "report").Total, "not after an edit");
        Check.True(index.Find(ws, "memo").Tasks.Single().Item == t, "found by its new text");
        ws.Delete(t);
        Check.Equal(0, index.Find(ws, "memo").Total, "gone once deleted");
    }

    private static void Quick()
    {
        var ws = BigWorkspace();
        var index = new SearchIndex();
        var first = System.Diagnostics.Stopwatch.StartNew();
        index.Find(ws, "zzz");
        long firstMs = first.ElapsedMilliseconds;
        var typing = System.Diagnostics.Stopwatch.StartNew();
        int found = 0;
        string[] queries = ["c", "cl", "cla", "clas", "clash", "facade", "sheet 49", "nothing like this"];
        foreach (string q in queries) found += index.Find(ws, q).Total;
        double perQuery = typing.ElapsedMilliseconds / (double)queries.Length;
        Check.True(found > 10_000, $"found plenty ({found})");
        // Generous budgets, to catch something accidentally slow (folding again on every keystroke).
        Check.True(firstMs < 3000, $"the first query, folding everything ({firstMs} ms)");
        Check.True(perQuery < 150, $"each query after it ({perQuery:F1} ms)");
    }

    /// <summary>10,000 tasks and 500 notes of 200 lines each (about seven million characters), as the speed checks use.</summary>
    internal static Workspace BigWorkspace()
    {
        var ws = new Workspace(new Settings(), new Hlc("search04"), WorkspaceData.Empty);
        string[] words = ["clash", "report", "façade", "Ducts", "beams", "model", "Navisworks", "level", "review", "Revit"];
        for (int i = 0; i < 10_000; i++) ws.AddTask($"{words[i % 10]} {words[i * 7 % 10]} task number {i}");
        for (int i = 0; i < 500; i++)
        {
            var lines = Enumerable.Range(0, 200).Select(j => $"- {words[(i + j) % 10]} item {j} on sheet {i}, with some more words to make it long");
            ws.AddSheet(null, $"Sheet {i}\r\n{string.Join("\r\n", lines)}");
        }
        return ws;
    }
}
