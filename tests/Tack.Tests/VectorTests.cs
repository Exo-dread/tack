using System.IO;
using System.Text;
using System.Text.Json;
using Tack.Model;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>
/// Replays tests/vectors/sync.json, which Tack Focus's own model computed
/// (web/tests/unit/vectors.test.ts), and checks the C# port gets exactly the same results.
/// </summary>
internal static class VectorTests
{
    private static readonly Lazy<JsonElement> Vectors = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Program.Root, "tests", "vectors", "sync.json"))).RootElement);

    private static JsonElement V => Vectors.Value;

    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("stamps: encode", Encode);
        yield return ("stamps: decode, validate, read times", Decode);
        yield return ("hybrid logical clock", Clock);
        yield return ("order keys: after, before, between, at", Keys);
        yield return ("order keys: long runs and random inserts", KeySequences);
        yield return ("derived ids", DerivedIds);
        yield return ("JavaScript white space, trim and JSON.stringify", Text);
        yield return ("dates", Dates);
        yield return ("merge: tasks", MergeTasks);
        yield return ("merge: sheets (with conflict copies)", MergeSheets);
        yield return ("merge: focus", MergeFocus);
        int n = 0;
        foreach (var script in V.GetProperty("workspaces").EnumerateArray())
        {
            var s = script;
            yield return ($"workspace script {++n}: every op stamps, orders and merges like Tack Focus", () => Replay(s));
        }
        foreach (var c in V.GetProperty("files").EnumerateArray())
        {
            var file = c;
            yield return ($"import: {file.Str("name")}", () => Import(file));
        }
    }

    private static void Encode()
    {
        foreach (var c in V.GetProperty("stamps").GetProperty("encode").EnumerateArray())
            Check.Equal(c.Str("stamp"), Stamp.Encode(c.Long("ms"), c.Long("counter"), c.Str("node")!), "encode");
    }

    private static void Decode()
    {
        var stamps = V.GetProperty("stamps");
        long now = stamps.Long("now");
        foreach (var c in stamps.GetProperty("decode").EnumerateArray())
        {
            string s = c.Str("stamp")!;
            var value = c.GetProperty("value");
            var d = Stamp.Decode(s);
            if (value.ValueKind == JsonValueKind.Null) Check.Equal(null, d, $"decode {s}");
            else
            {
                Check.True(d != null, $"decode {s}: expected a value");
                Check.Equal(value.GetProperty("ms").GetDouble(), d!.Value.Ms, $"decode {s}: ms");
                Check.Equal(value.GetProperty("counter").GetDouble(), d.Value.Counter, $"decode {s}: counter");
                Check.Equal(value.Str("node"), d.Value.Node, $"decode {s}: node");
            }
            Check.Equal(c.GetProperty("time").GetDouble(), Stamp.Time(s), $"time {s}");
            Check.Equal(c.GetProperty("isStamp").GetBoolean(), Stamp.IsStamp(s), $"isStamp {s}");
            Check.Equal(c.GetProperty("plausible").GetBoolean(), Stamp.IsPlausible(s, now), $"plausible {s}");
        }
    }

    private static void Clock()
    {
        foreach (var script in V.GetProperty("hlc").EnumerateArray())
        {
            long wall = 0;
            var clock = new Hlc(script.Str("node")!, () => wall);
            int i = 0;
            foreach (var step in script.GetProperty("steps").EnumerateArray())
            {
                wall = step.Long("wall");
                if (step.Str("now") is { } expected) Check.Equal(expected, clock.Now(), $"{script.Str("node")} step {i}");
                else clock.Observe(step.Str("observe")!);
                i++;
            }
        }
    }

    private static void Keys()
    {
        var order = V.GetProperty("order");
        foreach (var c in order.GetProperty("after").EnumerateArray())
            Check.Equal(c.Str("key"), OrderKey.After(c.Str("a")!), $"after {c.Str("a")}");
        foreach (var c in order.GetProperty("before").EnumerateArray())
            Check.Equal(c.Str("key"), OrderKey.Before(c.Str("b")!), $"before {c.Str("b")}");
        foreach (var c in order.GetProperty("between").EnumerateArray())
        {
            string? a = c.Str("a"), b = c.Str("b");
            if (c.TryGetProperty("error", out _)) Check.Throws<ArgumentException>(() => OrderKey.Between(a, b), $"between {a} {b}");
            else Check.Equal(c.Str("key"), OrderKey.Between(a, b), $"between {a} {b}");
        }
        foreach (var c in order.GetProperty("at").EnumerateArray())
        {
            var keys = c.GetProperty("keys").EnumerateArray().Select(k => k.GetString()!).ToList();
            Check.Equal(c.Str("key"), OrderKey.At(keys, c.GetProperty("index").GetInt32()), $"at [{string.Join(",", keys)}] {c.GetProperty("index")}");
        }
        foreach (var c in order.GetProperty("isOrderKey").EnumerateArray())
            Check.Equal(c.GetProperty("valid").GetBoolean(), OrderKey.IsOrderKey(c.Str("value")), $"isOrderKey {c.Str("value")}");
    }

    private static void KeySequences()
    {
        var order = V.GetProperty("order");
        string top = OrderKey.Mid, bottom = OrderKey.Mid;
        int i = 0;
        foreach (var k in order.GetProperty("tops").EnumerateArray()) Check.Equal(k.GetString(), top = OrderKey.Before(top), $"top {i++}");
        i = 0;
        foreach (var k in order.GetProperty("bottoms").EnumerateArray()) Check.Equal(k.GetString(), bottom = OrderKey.After(bottom), $"bottom {i++}");
        var keys = new List<string>();
        i = 0;
        foreach (var c in order.GetProperty("inserts").EnumerateArray())
        {
            int index = c.GetProperty("index").GetInt32();
            string key = OrderKey.At(keys, index);
            Check.Equal(c.Str("key"), key, $"insert {i++} at {index}");
            keys.Insert(index, key);
        }
    }

    private static void DerivedIds()
    {
        foreach (var c in V.GetProperty("derivedIds").EnumerateArray())
            Check.Equal(c.Str("id"), Ids.Derived(Json.FromUnits(c.GetProperty("units"))), "derivedId");
    }

    private static void Text()
    {
        var text = V.GetProperty("text");
        foreach (var c in text.GetProperty("whitespace").EnumerateArray())
        {
            int code = c.GetProperty("code").GetInt32();
            Check.Equal(c.GetProperty("white").GetBoolean(), JsText.IsWhiteSpace((char)code), $"white space U+{code:X4}");
        }
        foreach (var c in text.GetProperty("clean").EnumerateArray())
        {
            string t = c.Str("text")!;
            Check.Equal(c.Str("clean"), JsText.CleanTaskText(t), $"cleanTaskText {JsonSerializer.Serialize(t)}");
            Check.Equal(c.Str("trim"), JsText.Trim(t), $"trim {JsonSerializer.Serialize(t)}");
        }
        foreach (var c in text.GetProperty("stringify").EnumerateArray())
            Check.Equal(c.Str("json"), JsText.Stringify(Json.FromUnits(c.GetProperty("units"))), "JSON.stringify");
    }

    private static void Dates()
    {
        foreach (var c in V.GetProperty("dates").EnumerateArray())
        {
            long? expected = c.Has("ms") ? c.Long("ms") : null;
            Check.Equal(expected, IsoTime.ParseMs(c.Str("value")), $"parseDate {c.Str("value")}");
        }
    }

    private static void MergeTasks()
    {
        int i = 0;
        foreach (var c in V.GetProperty("merge").GetProperty("tasks").EnumerateArray())
        {
            var (value, changed) = Merge.Task(Json.Task(c.GetProperty("local")), Json.Task(c.GetProperty("remote"))!);
            Check.Equal(Json.Task(c.GetProperty("value")), value, $"task merge {i}");
            Check.Equal(c.GetProperty("changed").GetBoolean(), changed, $"task merge {i}: changed");
            i++;
        }
    }

    private static void MergeSheets()
    {
        int i = 0;
        foreach (var c in V.GetProperty("merge").GetProperty("sheets").EnumerateArray())
        {
            var (value, changed, conflict) = Merge.Sheet(Json.Sheet(c.GetProperty("local")), Json.Sheet(c.GetProperty("remote"))!, c.Str("base"));
            Check.Equal(Json.Sheet(c.GetProperty("value")), value, $"sheet merge {i}");
            Check.Equal(c.GetProperty("changed").GetBoolean(), changed, $"sheet merge {i}: changed");
            Check.Equal(Json.Sheet(c.GetProperty("conflict")), conflict, $"sheet merge {i}: conflict copy");
            i++;
        }
    }

    private static void MergeFocus()
    {
        int i = 0;
        foreach (var c in V.GetProperty("merge").GetProperty("focus").EnumerateArray())
        {
            var (value, changed) = Merge.Focus(Json.Focus(c.GetProperty("local")), Json.Focus(c.GetProperty("remote")));
            Check.Equal(Json.Focus(c.GetProperty("value")), value, $"focus merge {i}");
            Check.Equal(c.GetProperty("changed").GetBoolean(), changed, $"focus merge {i}: changed");
            i++;
        }
    }

    /// <summary>The same ops, on the desktop's workspace: lists after every op, all records at checkpoints and at the end.</summary>
    private static void Replay(JsonElement script)
    {
        var ops = script.GetProperty("ops").EnumerateArray().ToList();
        // The ids Tack Focus gave new tasks and sheets, in the order they were made.
        var ids = new Queue<string>();
        foreach (var op in ops)
        {
            if (op.Str("op") is "add" or "addNow" or "addSheet") ids.Enqueue(op.Str("id")!);
            if (op.Str("ensured") is { } ensured) ids.Enqueue(ensured);
        }

        long wall = ops[0].Long("wall");
        var ws = new Workspace(new Settings(), new Hlc(script.Str("node")!, () => wall), WorkspaceData.Empty,
            () => DateTimeOffset.FromUnixTimeMilliseconds(wall), ids.Dequeue);
        var undos = new Stack<Action>();
        WorkspaceData? snapshot = null;

        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            wall = op.Long("wall");
            string name = op.Str("op")!;
            TaskItem Task() => ws.TaskById(op.Str("id")!) ?? throw new TestFailure($"op {i} ({name}): no task {op.Str("id")}");
            NoteSheet Sheet() => ws.SheetById(op.Str("id")!) ?? throw new TestFailure($"op {i} ({name}): no sheet {op.Str("id")}");
            switch (name)
            {
                case "start": break;
                case "add": ws.AddTask(op.Str("text")!); break;
                case "addNow": ws.AddTask(op.Str("text")!, asNow: true); break;
                case "edit": Task().Text = op.Str("text")!; break;
                case "complete": ws.Complete(Task()); break;
                case "reopen": ws.Reopen(Task()); break;
                case "setNow": ws.SetNow(Task()); break;
                case "clearNow": ws.ClearNow(); break;
                case "move": ws.MoveOpen(ws.Open.IndexOf(Task()), op.GetProperty("index").GetInt32()); break;
                case "delete": undos.Push(ws.Delete(Task())); break;
                case "clearDone": undos.Push(ws.ClearDone()); break;
                case "undo": undos.Pop()(); break;
                case "addSheet": ws.AddSheet(); break;
                case "editSheet": Sheet().Text = op.Str("text")!; break;
                case "deleteSheet": undos.Push(ws.DeleteSheet(Sheet())); break;
                case "snapshot": snapshot = ws.ToData(); break;
                case "restore": ws.Restore(snapshot!); break;
                case "merge": ws.MergeFrom(Json.Data(op.GetProperty("data")), op.Str("base")); break;
                default: throw new TestFailure($"unknown op {name}");
            }
            Check.Equal(Json.Views(op.GetProperty("views")), Json.Views(ws), $"op {i} ({name}): lists");
            if (op.TryGetProperty("check", out var check)) Check.Data(Json.Data(check), ws.ToData(), $"op {i} ({name}): records");
        }
        Check.True(ids.Count == 0, "every id was used");
        Check.Data(Json.Data(script.GetProperty("data")), ws.ToData(), "final records");
    }

    /// <summary>A file imported into a workspace, as Settings → Import does it.</summary>
    private static void Import(JsonElement c)
    {
        long wall = c.Long("wall");
        var clock = new Hlc(c.Str("node")!, () => wall);
        var ws = new Workspace(new Settings(), clock, Json.Data(c.GetProperty("local")),
            () => DateTimeOffset.FromUnixTimeMilliseconds(wall), () => throw new TestFailure("no new ids expected"));
        // Where Tack Focus's clock stood (it may have seen stamps the data no longer holds).
        var state = c.GetProperty("clock");
        clock.Observe(Stamp.Encode(state.Long("ms"), state.Long("counter"), ""));

        string? error = null;
        try
        {
            using var doc = TackFile.Parse(Encoding.UTF8.GetBytes(c.Str("file")!));
            ws.Import(doc.RootElement, c.Has("lastModified") ? c.Long("lastModified") : null);
        }
        catch (TackFileException ex)
        {
            error = ex.Message;
        }
        Check.Equal(c.Str("error"), error, "error message");
        Check.Data(Json.Data(c.GetProperty("result")), ws.ToData(), "records");
        Check.Equal(Json.Views(c.GetProperty("views")), Json.Views(ws), "lists");
    }
}
