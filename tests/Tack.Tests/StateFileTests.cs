using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Tack.Model;
using Tack.Services;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>tack.json itself: old files, Tack Focus exports, hand edits, damage, import.</summary>
internal static class StateFileTests
{
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string VectorsDir => Path.Combine(Program.Root, "tests", "vectors");

    private static JsonElement Vectors => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(VectorsDir, "sync.json"))).RootElement;

    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("tack.json from Tack 1.1 opens unchanged and keeps its top level byte for byte", LegacyFileLoadsUnchanged);
        yield return ("a Tack 1.1 file gets the very records Tack Focus gives it", LegacyRecordsMatchTackFocus);
        yield return ("Tack 1.1 can still read the new file", OlderVersionsReadNewFile);
        yield return ("a Tack Focus export opens as tack.json and saves back the same records", RoundTrip);
        yield return ("a Tack Focus export with spaces opens as tack.json and saves back the same section, key for key", RoundTripWithSpaces);
        yield return ("saving and opening again changes nothing", FixedPoint);
        yield return ("hand edits to the top level win, as fresh edits", HandEdits);
        yield return ("a damaged tackFocus section never stops tack.json from loading", DamagedSection);
        yield return ("a file saved by an older Tack (section dropped) loads as before", SectionDropped);
        yield return ("notes keep \"\\n\" in the records and the TextBox's \"\\r\\n\" on screen and in the top level", LineBreaks);
        yield return ("Import merges a Tack Focus export, and Undo puts things back", ImportAndUndo);
        yield return ("deletions are forgotten after 90 days", PurgesOldTombstones);
        yield return ("dates are written the way System.Text.Json writes them", DateFormat);
    }

    // ------------------------------------------------------------------ old files

    private static void LegacyFileLoadsUnchanged()
    {
        string legacy = File.ReadAllText(Path.Combine(VectorsDir, "legacy-1.1.json"));
        using var dir = new TempFolder(legacy);
        var (store, ws) = dir.Open();

        // What 1.1 shows: the now task, the rest of the open tasks in file order, done most recent first.
        using var doc = JsonDocument.Parse(legacy);
        var tasks = doc.RootElement.GetProperty("tasks").EnumerateArray().ToList();
        Check.Equal(tasks.First(t => t.GetProperty("now").GetBoolean()).Str("id"), ws.Now?.Id, "now");
        Check.Sequence(tasks.Where(t => !t.Has("completed") && !t.GetProperty("now").GetBoolean()).Select(t => t.Str("id")).ToList(),
            ws.Open.Select(t => (string?)t.Id).ToList(), "open");
        Check.Sequence(tasks.Where(t => t.Has("completed")).Select(t => t.Str("id")).ToList(), ws.Done.Select(t => (string?)t.Id).ToList(), "done");
        Check.Sequence(doc.RootElement.GetProperty("sheets").EnumerateArray().Select(s => s.Str("id")).ToList(),
            ws.Sheets.Select(s => (string?)s.Id).ToList(), "sheets");

        store.Flush();
        string saved = Lf(dir.Text);
        string before = Lf(legacy).TrimEnd();
        string prefix = before[..before.LastIndexOf('}')].TrimEnd() + ",\n  \"tackFocus\": {";
        Check.True(saved.StartsWith(prefix, StringComparison.Ordinal), "the top level is exactly what Tack 1.1 wrote, then the section:\n" + saved[..Math.Min(saved.Length, 400)]);
    }

    private static void LegacyRecordsMatchTackFocus()
    {
        var legacy = Vectors.GetProperty("legacy");
        using var dir = new TempFolder(legacy.Str("file")!);
        var store = new StateStore(dir.Path);
        var doc = store.Load();
        var data = StateFile.ToData(doc, new Hlc("desk"), Now, Now);
        var expected = Json.Data(legacy.GetProperty("records"));
        Check.Sequence(expected.Tasks, data.Tasks, "tasks");
        Check.Sequence(expected.Sheets, data.Sheets, "sheets");
    }

    private static void OlderVersionsReadNewFile()
    {
        string legacy = File.ReadAllText(Path.Combine(VectorsDir, "legacy-1.1.json"));
        using var dir = new TempFolder(legacy);
        var (store, ws) = dir.Open();
        ws.AddTask("added by the new version");
        store.Flush();

        // Tack 1.1's document shape and serializer options, which skip properties they don't know.
        var old = JsonSerializer.Deserialize(dir.Bytes(), LegacyJsonContext.Default.LegacyDocument)!;
        var ids = new List<string>();
        if (ws.Now != null) ids.Add(ws.Now.Id);
        ids.AddRange(ws.Open.Select(t => t.Id));
        ids.AddRange(ws.Done.Select(t => t.Id));
        Check.Sequence(ids, old.Tasks.Select(t => t.Id).ToList(), "tasks as 1.1 reads them");
        Check.Equal("added by the new version", old.Tasks.First(t => !t.Now).Text, "new task");
        Check.Sequence(ws.Sheets.Select(s => s.Text).ToList(), old.Sheets.Select(s => s.Text).ToList(), "sheets as 1.1 reads them");
        Check.Equal("dark", old.Settings.Theme, "settings");
    }

    // ------------------------------------------------------------------ Tack Focus exports

    private static void RoundTrip()
    {
        var rt = Vectors.GetProperty("roundTrip");
        string export = rt.Str("file")!;
        using var dir = new TempFolder(export);
        var (store, ws) = dir.Open();

        using var exported = JsonDocument.Parse(export);
        var records = TackFile.ReadSection(exported.RootElement.GetProperty("tackFocus"), Now, clean: false)!;
        Check.Equal(Json.Views(rt.GetProperty("views")), Json.Views(ws), "lists");
        Check.Data(records, ws.ToData(), "records as loaded");

        store.Flush();
        using var saved = JsonDocument.Parse(dir.Bytes());
        var back = TackFile.ReadSection(saved.RootElement.GetProperty("tackFocus"), Now, clean: false)!;
        Check.Data(records, back, "records as saved");

        // The top level shows the same lists, with the same dates.
        var webTop = exported.RootElement.GetProperty("tasks").EnumerateArray().ToList();
        var deskTop = saved.RootElement.GetProperty("tasks").EnumerateArray().ToList();
        Check.Equal(webTop.Count, deskTop.Count, "top-level tasks");
        for (int i = 0; i < webTop.Count; i++)
        {
            Check.Equal(webTop[i].Str("id"), deskTop[i].Str("id"), $"task {i}: id");
            Check.Equal(webTop[i].Str("text"), deskTop[i].Str("text"), $"task {i}: text");
            Check.Equal(webTop[i].GetProperty("now").GetBoolean(), deskTop[i].GetProperty("now").GetBoolean(), $"task {i}: now");
            Check.Equal(IsoTime.ParseMs(webTop[i].Str("created")), IsoTime.ParseMs(deskTop[i].Str("created")), $"task {i}: created");
            Check.Equal(IsoTime.ParseMs(webTop[i].Str("completed")), IsoTime.ParseMs(deskTop[i].Str("completed")), $"task {i}: completed");
        }

        // tests/vectors/desktop-export.json is what Tack Focus's tests read back (the "and back" half).
        string fixture = Path.Combine(VectorsDir, "desktop-export.json");
        string normalized = Regex.Replace(Lf(dir.Text), "\"device\": \"[^\"]*\"", "\"device\": \"desktop1\"");
        if (Program.Update) File.WriteAllText(fixture, normalized, new UTF8Encoding(false));
        else Check.Equal(Lf(File.ReadAllText(fixture)), normalized, "tests/vectors/desktop-export.json is up to date (run with --update)");
    }

    private static void RoundTripWithSpaces()
    {
        // Exported by Tack Focus with spaces, a deleted one, moves (one back to Main) and a deleted task in a space.
        string export = Vectors.GetProperty("files").EnumerateArray().First(f => f.Str("name") == "an export with spaces, into a fresh workspace").Str("file")!;
        using var dir = new TempFolder(export);
        var (store, ws) = dir.Open();
        store.Flush();

        using var exported = JsonDocument.Parse(export);
        using var saved = JsonDocument.Parse(dir.Bytes());
        var web = exported.RootElement.GetProperty("tackFocus");
        var desk = saved.RootElement.GetProperty("tackFocus");
        Check.True(web.GetProperty("spaces").GetArrayLength() > 1 && web.GetProperty("tasks").EnumerateArray().Any(t => t.Has("space")), "the export has spaces and assignments");
        foreach (string key in new[] { "focus", "tasks", "sheets", "spaces" })
            Check.Equal(Ordered(web.GetProperty(key)), Ordered(desk.GetProperty(key)), $"the section's {key}, key for key");
        // The top level still lists every live task, whatever its space.
        Check.Equal(
            string.Join(",", exported.RootElement.GetProperty("tasks").EnumerateArray().Select(t => t.Str("id"))),
            string.Join(",", saved.RootElement.GetProperty("tasks").EnumerateArray().Select(t => t.Str("id"))),
            "top-level tasks");
    }

    /// <summary>A JSON value with its keys in the order they were written.</summary>
    internal static string Ordered(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().Select(p => JsonSerializer.Serialize(p.Name) + ":" + Ordered(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Ordered)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(e.GetString()),
        _ => e.GetRawText(),
    };

    private static void FixedPoint()
    {
        foreach (string source in new[] { File.ReadAllText(Path.Combine(VectorsDir, "legacy-1.1.json")), Vectors.GetProperty("roundTrip").Str("file")! })
        {
            using var dir = new TempFolder(source);
            var (store, ws) = dir.Open();
            store.Flush();
            byte[] first = dir.Bytes();
            var data = ws.ToData();

            var (again, ws2) = dir.Open();
            again.Flush();
            Check.True(first.AsSpan().SequenceEqual(dir.Bytes()), "opening the saved file and saving again writes the same bytes");
            Check.Data(data, ws2.ToData(), "same records");
            Check.Equal(ws.Clock.Node, ws2.Clock.Node, "the device id is kept");
        }
    }

    private static void HandEdits()
    {
        using var dir = new TempFolder(Vectors.GetProperty("roundTrip").Str("file")!);
        var (store, ws) = dir.Open();
        store.Flush();
        var before = ws.ToData();
        string renamed = ws.Open[0].Id, removed = ws.Open[1].Id, newNow = ws.Open[2].Id, oldNow = ws.Now!.Id;
        string sheetEdited = ws.Sheets[0].Id, sheetRemoved = ws.Sheets[1].Id;

        // Edit the top level the way someone might in Notepad.
        var root = System.Text.Json.Nodes.JsonNode.Parse(dir.Text)!;
        var tasks = root["tasks"]!.AsArray();
        foreach (var t in tasks)
        {
            string id = (string)t!["id"]!;
            if (id == renamed) t["text"] = "Renamed by hand";
            if (id == oldNow) t["now"] = false;
            if (id == newNow) t["now"] = true;
        }
        tasks.Remove(tasks.First(t => (string)t!["id"]! == removed));
        var sheets = root["sheets"]!.AsArray();
        sheets.First(s => (string)s!["id"]! == sheetEdited)!["text"] = "Edited by hand";
        sheets.Remove(sheets.First(s => (string)s!["id"]! == sheetRemoved));
        File.WriteAllText(dir.File, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var expectedOpen = tasks.Where(t => t!["completed"] == null && !(bool)t["now"]!).Select(t => (string)t!["id"]!).ToList();

        var (again, ws2) = dir.Open();
        Check.Equal(newNow, ws2.Now?.Id, "now moved");
        Check.Sequence(expectedOpen, ws2.Open.Select(t => t.Id).ToList(), "open tasks in the file's order");
        Check.True(ws2.TaskById(removed)!.IsDeleted, "the removed task is a tombstone");
        Check.Equal("Renamed by hand", ws2.TaskById(renamed)!.Text, "renamed");
        Check.Equal("Edited by hand", ws2.SheetById(sheetEdited)!.Text, "sheet edited");
        Check.True(ws2.SheetById(sheetRemoved)!.IsDeleted, "the removed sheet is a tombstone");

        // Each change has a clock newer than anything before it; records nobody touched are as they were.
        string newest = before.Focus.Clk;
        foreach (var t in before.Tasks) newest = Stamp.Max(newest, t.Clk.Max());
        foreach (var x in before.Sheets) newest = Stamp.Max(newest, x.Clk.Max());
        var after = ws2.ToData();
        Check.True(Stamp.IsLater(after.Tasks.First(t => t.Id == renamed).Clk.Text, newest), "rename stamped");
        Check.True(Stamp.IsLater(after.Tasks.First(t => t.Id == removed).Clk.Deleted, newest), "removal stamped");
        Check.True(Stamp.IsLater(after.Focus.Clk, newest), "focus stamped");
        Check.True(Stamp.IsLater(after.Sheets.First(x => x.Id == sheetEdited).Clk.Text, newest), "sheet edit stamped");
        foreach (var t in before.Tasks.Where(t => t.Deleted || t.Completed != null))
            Check.Equal(t, after.Tasks.First(x => x.Id == t.Id), "untouched record");

        again.Flush();
        byte[] saved = dir.Bytes();
        var (third, _) = dir.Open();
        third.Flush();
        Check.True(saved.AsSpan().SequenceEqual(dir.Bytes()), "after taking the hand edits, the file is stable again");
    }

    private static void DamagedSection()
    {
        string export = Vectors.GetProperty("roundTrip").Str("file")!;
        using var exported = JsonDocument.Parse(export);
        var expected = Json.Views(Vectors.GetProperty("roundTrip").GetProperty("views"));
        foreach (string damage in new[]
        {
            "\"garbage\"",
            "null",
            "{ \"tasks\": 5, \"sheets\": [] }",
            "{ \"format\": 1, \"focus\": { \"id\": 7 }, \"tasks\": [5, { \"id\": 7 }, { \"id\": \"x\", \"clk\": {} }], \"sheets\": [true] }",
        })
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(export)!;
            root["tackFocus"] = System.Text.Json.Nodes.JsonNode.Parse(damage);
            using var dir = new TempFolder(root.ToJsonString());
            var (store, ws) = dir.Open();
            Check.Equal(expected, Json.Views(ws), $"lists with tackFocus = {damage}");
            store.Flush();
        }

        // Comments and trailing commas inside the section, as a hand edit might leave them.
        string section = exported.RootElement.GetProperty("tackFocus").GetRawText();
        string commented = export.Replace(section, section.Replace("\"format\": 1,", "/* edited */ \"format\": 1, // by hand\n").TrimEnd('}') + ",}");
        Check.True(commented.Contains("/* edited */") && commented.Contains(",}"), "the section was edited");
        using (var dir = new TempFolder(commented))
        {
            var (_, ws) = dir.Open();
            Check.Equal(expected, Json.Views(ws), "lists with comments in the section");
            Check.Data(TackFile.ReadSection(exported.RootElement.GetProperty("tackFocus"), Now, clean: false)!, ws.ToData(), "records with comments in the section");
        }
    }

    private static void SectionDropped()
    {
        using var dir = new TempFolder(Vectors.GetProperty("roundTrip").Str("file")!);
        var (store, ws) = dir.Open();
        store.Flush();
        string views = Json.Views(ws);
        var root = System.Text.Json.Nodes.JsonNode.Parse(dir.Text)!.AsObject();
        root.Remove("tackFocus");
        File.WriteAllText(dir.File, root.ToJsonString());
        var (_, ws2) = dir.Open();
        Check.Equal(views, Json.Views(ws2), "lists");
    }

    private static void LineBreaks()
    {
        // Tack 1.1 saved the note as its TextBox has it, with "\r\n".
        using var dir = new TempFolder(File.ReadAllText(Path.Combine(VectorsDir, "legacy-1.1.json")));
        var (store, ws) = dir.Open();
        var site = ws.Sheets[0];
        Check.Equal("Site visit\r\n- rebar\r\n- formwork", site.Text, "the TextBox gets the note as it was");
        var record = ws.ToData().Sheets.First(s => s.Id == site.Id);
        Check.Equal("Site visit\n- rebar\n- formwork", record.Text, "the record has \"\\n\", as Tack Focus has it");

        store.Flush();
        using (var saved = JsonDocument.Parse(dir.Bytes()))
        {
            var root = saved.RootElement;
            Check.Equal(site.Text, root.GetProperty("sheets")[0].Str("text"), "the top level keeps \"\\r\\n\", for Tack 1.1");
            var section = root.GetProperty("tackFocus").GetProperty("sheets").EnumerateArray().First(s => s.Str("id") == site.Id);
            Check.Equal(record.Text, section.Str("text"), "the section has \"\\n\"");
        }
        var (_, ws2) = dir.Open();
        Check.Data(ws.ToData(), ws2.ToData(), "opening it again: line breaks that differ are no hand edit");

        // The same note from Tack Focus changes nothing; an edit there reaches the TextBox with "\r\n" and no copy.
        Check.True(!ws2.MergeFrom(new WorkspaceData([], [record], FocusData.None, []), record.Clk.Text), "the same note: nothing new");
        string phone = Stamp.Encode(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000, 0, "phone");
        var edited = record with { Text = record.Text + "\n- ties", Clk = record.Clk with { Text = phone } };
        int sheets = ws2.Sheets.Count;
        Check.True(ws2.MergeFrom(new WorkspaceData([], [edited], FocusData.None, []), record.Clk.Text), "an edit from Tack Focus");
        Check.Equal("Site visit\r\n- rebar\r\n- formwork\r\n- ties", ws2.SheetById(site.Id)!.Text, "reaches the TextBox with \"\\r\\n\"");
        Check.Equal(edited, ws2.ToData().Sheets.First(s => s.Id == site.Id), "and the record is Tack Focus's");
        Check.Equal(sheets, ws2.Sheets.Count, "no copy");
    }

    // ------------------------------------------------------------------ import

    private static void ImportAndUndo()
    {
        using var dir = new TempFolder(File.ReadAllText(Path.Combine(VectorsDir, "legacy-1.1.json")));
        // Saved long before the export (a 1.1 file's focus is as old as the file).
        File.SetLastWriteTimeUtc(dir.File, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var (_, ws) = dir.Open();
        string before = Json.Views(ws);
        int open = ws.OpenCount, sheets = ws.Sheets.Count;

        var rt = Vectors.GetProperty("roundTrip");
        byte[] export = Encoding.UTF8.GetBytes(rt.Str("file")!);
        var (message, undo) = FileImport.Run(ws, export, rt.Long("wall"));
        var views = rt.GetProperty("views");
        int addedTasks = views.GetProperty("open").GetArrayLength() + views.GetProperty("done").GetArrayLength() + (views.Has("now") ? 1 : 0);
        int addedSheets = views.GetProperty("sheets").GetArrayLength();
        Check.Equal($"Imported {addedTasks} tasks and {addedSheets} sheets", message, "message");
        Check.True(undo != null, "can undo");
        Check.Equal(views.Str("now"), ws.Now?.Id, "the export's focus task is now the focus here");
        Check.Equal(open + views.GetProperty("open").GetArrayLength() + (views.Has("now") ? 1 : 0), ws.OpenCount, "open tasks from both");
        Check.Equal(sheets + addedSheets, ws.Sheets.Count, "sheets from both");

        var (again, noUndo) = FileImport.Run(ws, export, rt.Long("wall"));
        Check.Equal("Nothing new in that file", again, "importing again");
        Check.True(noUndo == null, "nothing to undo");

        undo!();
        Check.Equal(before, Json.Views(ws), "undo puts the lists back");

        var (bad, _) = FileImport.Run(ws, Encoding.UTF8.GetBytes("{ \"hello\": 1 }"), null);
        Check.Equal("This doesn't look like a Tack file.", bad, "not a Tack file");
    }

    private static void PurgesOldTombstones()
    {
        long wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string old = Stamp.Encode(wall - (long)TimeSpan.FromDays(91).TotalMilliseconds, 0, "x");
        string recent = Stamp.Encode(wall - (long)TimeSpan.FromDays(10).TotalMilliseconds, 0, "x");
        var tombstone = (string id, string clk) => new TaskData(id, "", "2026-01-01T00:00:00.000+00:00", null, "V", true, "", TaskClocks.All(clk));
        var data = new WorkspaceData([tombstone("oldoldoldold", old), tombstone("recentrecent", recent)], [], FocusData.None, []);
        var ws = new Workspace(new Settings(), new Hlc("x"), data);
        Check.True(ws.TaskById("oldoldoldold") == null, "a deletion 91 days old is forgotten");
        Check.True(ws.TaskById("recentrecent") != null, "a deletion 10 days old is kept");
    }

    private static void DateFormat()
    {
        foreach (var d in new[]
        {
            new DateTimeOffset(2026, 10, 17, 21, 41, 25, TimeSpan.FromHours(3)).AddTicks(3783258),
            new DateTimeOffset(2026, 10, 17, 21, 41, 25, TimeSpan.FromHours(3)).AddTicks(3790520),
            new DateTimeOffset(2026, 10, 17, 21, 41, 25, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, 600, TimeSpan.FromHours(-5)),
        })
        {
            string json = JsonSerializer.Serialize(d, LegacyJsonContext.Default.DateTimeOffset);
            Check.Equal(json.Trim('"'), IsoTime.FormatTrimmed(d), "same text as System.Text.Json");
            Check.Equal(d.ToUnixTimeMilliseconds(), IsoTime.ParseMs(IsoTime.FormatTrimmed(d)), "reads back");
        }
    }

    private static string Lf(string s) => s.Replace("\r\n", "\n");

    /// <summary>A throwaway data folder holding a tack.json.</summary>
    private sealed class TempFolder : IDisposable
    {
        public TempFolder(string tackJson)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tack-tests-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
            System.IO.File.WriteAllText(File, tackJson, new UTF8Encoding(false));
        }

        public string Path { get; }

        public string File => System.IO.Path.Combine(Path, "tack.json");

        public string Text => System.IO.File.ReadAllText(File);

        public byte[] Bytes() => System.IO.File.ReadAllBytes(File);

        public (StateStore Store, Workspace Ws) Open()
        {
            var store = new StateStore(Path);
            return (store, store.Attach(store.Load()));
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>tack.json as Tack 1.1 declared it (no tackFocus section).</summary>
internal sealed class LegacyDocument
{
    public int Version { get; set; } = 1;
    public List<TaskRecord> Tasks { get; set; } = new();
    public List<SheetRecord> Sheets { get; set; } = new();
    public Settings Settings { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LegacyDocument))]
[JsonSerializable(typeof(DateTimeOffset))]
internal partial class LegacyJsonContext : JsonSerializerContext;
