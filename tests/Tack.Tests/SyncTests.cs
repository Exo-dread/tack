using System.IO;
using System.Text;
using System.Text.Json;
using Tack.Model;
using Tack.Services;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>
/// Sync on Windows: the engine replays every cycle Tack Focus recorded (tests/vectors/sync-engine.json)
/// and must end with the same records, state and device file; device files are read the same way;
/// and the folder provider and the whole loop are checked against real folders on disk.
/// </summary>
internal static class SyncTests
{
    private static readonly Lazy<JsonElement> Vectors = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Program.Root, "tests", "vectors", "sync-engine.json"))).RootElement);

    private static readonly string DesktopDevice = Path.Combine(Program.Root, "tests", "vectors", "desktop-device.json");

    public static IEnumerable<(string Name, Action Run)> All()
    {
        int n = 0;
        foreach (var cycle in Vectors.Value.GetProperty("cycles").EnumerateArray())
        {
            var c = cycle;
            int i = ++n;
            yield return ($"sync cycle {i} ({c.Str("device")}): same merge, state and file as Tack Focus", () => Cycle(c, i));
        }
        foreach (var file in Vectors.Value.GetProperty("deviceFiles").EnumerateArray())
        {
            var f = file;
            yield return ($"device file: {f.Str("name")}", () => ReadDeviceFile(f));
        }
        yield return ("device file: Tack Focus reads what this app writes (desktop-device.json)", DesktopDeviceFile);
        yield return ("sync folder: whole-file writes, temp files never counted, missing folder", LocalFolder);
        yield return ("sync folder: two PCs through a folder on disk, notes kept twice, deletions kept", TwoPcs);
        yield return ("sync folder: finds Tack Focus's folder in OneDrive (localised Apps too)", FindsOneDriveFolder);
        yield return ("sync: deletions are never forgotten while syncing", KeepsDeletions);
        yield return ("sync: a new PC joining leaves its untouched welcome tasks and blank sheet behind", JoinsWithoutWelcome);
    }

    // ---------------------------------------------------------------- the shared vectors

    private static void Cycle(JsonElement c, int index)
    {
        string device = c.Str("device")!;
        long now = c.Long("now");
        var before = c.GetProperty("before");
        var after = c.GetProperty("after");

        var hlc = new Hlc(device, () => now);
        var state = c.GetProperty("hlc");
        hlc.Observe(Stamp.Encode(state.Long("ms"), state.Long("counter"), device));
        var ws = new Workspace(new Settings(), hlc, Json.Data(before.GetProperty("data")), () => DateTimeOffset.FromUnixTimeMilliseconds(now));
        var meta = JsonSerializer.Deserialize(before.GetProperty("meta").GetRawText(), SyncJsonContext.Default.SyncMeta)!;
        var folder = new MemoryFolder();
        foreach (var f in before.GetProperty("folder").EnumerateArray()) folder.Files[f.Str("name")!] = (f.Str("tag")!, Encoding.UTF8.GetBytes(f.Str("text")!));

        var engine = new SyncEngine(ws, folder, new SyncHooks
        {
            Label = () => ($"Device {device}", "Tack Focus test"),
            FlushAsync = () => Task.CompletedTask,
            SaveMetaAsync = _ => Task.CompletedTask,
        });
        string what = $"cycle {index}";
        if (after.TryGetProperty("collision", out _))
        {
            Check.Throws<DeviceCollisionException>(() => engine.CycleAsync(meta, now).GetAwaiter().GetResult(), $"{what}: another device writes as {device}");
            return;
        }
        var result = engine.CycleAsync(meta, now).GetAwaiter().GetResult();

        var expected = after.GetProperty("result");
        Check.Equal(string.Join(",", expected.GetProperty("merged").EnumerateArray().Select(x => x.GetString())), string.Join(",", result.Merged), $"{what}: merged");
        Check.Equal(expected.GetProperty("wrote").GetBoolean(), result.Wrote, $"{what}: wrote its file");
        Check.Equal(
            string.Join(",", expected.GetProperty("problems").EnumerateArray().Select(p => $"{p.Str("device")}:{p.Str("reason")}")),
            string.Join(",", result.Problems.Select(p => $"{p.Device}:{p.Problem.ToString().ToLowerInvariant()}")),
            $"{what}: problems");
        Check.Data(Sorted(Json.Data(after.GetProperty("data"))), Sorted(ws.ToData()), $"{what}: records");
        JsonEqual(after.GetProperty("meta"), JsonDocument.Parse(JsonSerializer.Serialize(meta, SyncJsonContext.Default.SyncMeta)).RootElement, $"{what}: sync state");
        if (after.TryGetProperty("written", out var written))
        {
            var (_, bytes) = folder.Files[DeviceFile.FileName(device)];
            JsonEqual(written, JsonDocument.Parse(bytes).RootElement, $"{what}: the device file it wrote");
        }
        if (index == Vectors.Value.GetProperty("cycles").GetArrayLength() && Program.Update) WriteDesktopDevice(after.GetProperty("data"), meta);
    }

    /// <summary>For Tack Focus's tests: a device file this app wrote, from the last cycle's records.</summary>
    private static void WriteDesktopDevice(JsonElement data, SyncMeta meta)
    {
        string written = meta.Own.Written ?? Stamp.Zero;
        byte[] file = DeviceFile.Write(Json.Data(data), "desk0001", "Tack for Windows on TEST-PC", "Tack for Windows 1.3.0", written, meta.Seen);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WritePropertyName("data");
            data.WriteTo(w);
            w.WriteStartObject("seen");
            foreach (var (k, v) in meta.Seen.OrderBy(e => e.Key, StringComparer.Ordinal)) w.WriteString(k, v);
            w.WriteEndObject();
            w.WriteString("written", written);
            w.WriteString("text", Encoding.UTF8.GetString(file));
            w.WriteEndObject();
        }
        File.WriteAllBytes(DesktopDevice, buffer.ToArray());
    }

    private static void DesktopDeviceFile()
    {
        if (!File.Exists(DesktopDevice)) throw new TestFailure("tests/vectors/desktop-device.json is missing: run with --update");
        var doc = JsonDocument.Parse(File.ReadAllBytes(DesktopDevice)).RootElement;
        var data = Json.Data(doc.GetProperty("data"));
        var seen = doc.GetProperty("seen").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        byte[] again = DeviceFile.Write(data, "desk0001", "Tack for Windows on TEST-PC", "Tack for Windows 1.3.0", doc.Str("written")!, seen);
        Check.Equal(doc.Str("text"), Encoding.UTF8.GetString(again), "the file this app writes is stable (rewrite with --update if the format changed on purpose)");
    }

    private static void ReadDeviceFile(JsonElement f)
    {
        var expected = f.GetProperty("expect");
        string what = f.Str("name")!;
        try
        {
            var file = DeviceFile.Parse(Encoding.UTF8.GetBytes(f.Str("text")!), f.Str("device")!, f.Long("now"));
            if (expected.Has("problem")) throw new TestFailure($"{what}: expected {expected.Str("problem")}, but it was read");
            var e = expected.GetProperty("file");
            Check.Equal(e.Str("name"), file.Name, $"{what}: name");
            Check.Equal(e.Str("app"), file.App, $"{what}: app");
            Check.Equal(e.Str("written"), file.Written, $"{what}: written");
            Check.Equal(
                string.Join(",", e.GetProperty("seen").EnumerateObject().Select(p => $"{p.Name}={p.Value.GetString()}").Order(StringComparer.Ordinal)),
                string.Join(",", file.Seen.Select(p => $"{p.Key}={p.Value}").Order(StringComparer.Ordinal)),
                $"{what}: seen");
            Check.Data(Sorted(Json.Data(e)), Sorted(new WorkspaceData(file.Tasks, file.Sheets, file.Focus)), $"{what}: records");
        }
        catch (DeviceFileException ex)
        {
            Check.Equal(expected.Str("problem"), ex.Problem.ToString().ToLowerInvariant(), $"{what}: problem");
        }
    }

    // ---------------------------------------------------------------- the folder on disk

    private static void LocalFolder()
    {
        using var temp = new TempDir();
        var missing = new LocalSyncFolder(Path.Combine(temp.Path, "nope"));
        Check.Throws<SyncException>(() => missing.ListAsync().GetAwaiter().GetResult(), "a folder that isn't there");

        var folder = new LocalSyncFolder(temp.Path);
        folder.Prepare();
        Check.True(File.Exists(Path.Combine(temp.Path, "tack-sync.json")) && File.Exists(Path.Combine(temp.Path, "README.txt")), "marker and README");
        Check.True(LocalSyncFolder.IsSyncFolder(temp.Path), "recognised as a sync folder");

        var first = folder.WriteAsync("abcd1234.json", "one"u8.ToArray()).GetAwaiter().GetResult();
        var second = folder.WriteAsync("abcd1234.json", "two, longer"u8.ToArray()).GetAwaiter().GetResult();
        Check.True(first.Tag != second.Tag, "the tag changes with the content");
        Check.Equal("abcd1234.json", string.Join(",", Directory.EnumerateFiles(folder.Devices).Select(Path.GetFileName)), "no temp file left behind by writes");
        // A sync client's conflict copy and a temp file sit beside it, and don't count.
        File.WriteAllText(Path.Combine(folder.Devices, "abcd1234-DESKTOP-PC.json"), "{}");
        File.WriteAllText(Path.Combine(folder.Devices, "~$abcd1234.json.tmp"), "half");
        var list = folder.ListAsync().GetAwaiter().GetResult();
        Check.Equal("abcd1234.json", string.Join(",", list.Select(e => e.Name).Where(n => DeviceFile.DeviceOfFile(n) != null)), "device files");
        Check.Equal("two, longer", Encoding.UTF8.GetString(folder.ReadAsync(list.First(e => e.Name == "abcd1234.json")).GetAwaiter().GetResult()), "read back");
        File.Delete(Path.Combine(folder.Devices, "abcd1234.json"));
        Check.Throws<SyncException>(() => folder.ReadAsync(new FolderEntry("abcd1234.json", "x")).GetAwaiter().GetResult(), "a file deleted after listing");
    }

    private static void TwoPcs()
    {
        using var temp = new TempDir();
        long wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Pc Make(string id) => new(id, temp.Path, () => wall);
        var a = Make("pcAAAAAA");
        var b = Make("pcBBBBBB");
        var note = a.Ws.AddSheet();
        note.Text = "Plan\n- one";
        a.Ws.AddTask("From PC A");
        a.Sync();
        b.Ws.AddTask("From PC B");
        b.Sync();
        a.Sync();
        Check.Equal("From PC A,From PC B", string.Join(",", a.Ws.Open.Select(t => t.Text).Order()), "both tasks on A");

        // The same note edited on both before either syncs: kept twice, on both.
        wall += 1000;
        a.Ws.SheetById(note.Id)!.Text = "Plan\n- one\n- two (A)";
        b.Ws.SheetById(note.Id)!.Text = "Plan\n- one\n- two (B)";
        // A deletion on one PC, an edit on the other made earlier.
        var shared = a.Ws.Open.First(t => t.Text == "From PC B");
        b.Ws.TaskById(shared.Id)!.Text = "From PC B, edited";
        wall += 1000;
        a.Ws.Delete(shared);
        for (int i = 0; i < 3; i++)
        {
            a.Sync();
            b.Sync();
        }
        Check.Equal(Json.Views(a.Ws), Json.Views(b.Ws), "both PCs show the same");
        Check.Equal(2, a.Ws.Sheets.Count(s => s.Text.StartsWith("Plan", StringComparison.Ordinal)), "the note kept twice");
        Check.True(a.Ws.Open.All(t => t.Id != shared.Id), "the later deletion wins over the earlier edit");
        Check.True(File.ReadAllText(Path.Combine(temp.Path, "devices", "pcAAAAAA.json")).Contains(shared.Id, StringComparison.Ordinal), "the deletion travels in the file");
    }

    private static void KeepsDeletions()
    {
        using var temp = new TempDir();
        long wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pc = new Pc("pcCCCCCC", temp.Path, () => wall);
        var t = pc.Ws.AddTask("Old news");
        pc.Ws.Delete(t);
        pc.Ws.KeepDeletions = () => true;
        wall += (long)TimeSpan.FromDays(120).TotalMilliseconds;
        pc.Ws.RefreshDay();
        Check.True(pc.Ws.TaskById(t.Id) != null, "kept while syncing");
        pc.Ws.KeepDeletions = () => false;
        pc.Ws.RefreshDay();
        Check.True(pc.Ws.TaskById(t.Id) == null, "forgotten after 90 days without sync");
    }

    private static void JoinsWithoutWelcome()
    {
        using var temp = new TempDir();
        long wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var phone = new Pc("phoneAAA", temp.Path, () => wall);
        var call = phone.Ws.AddTask("Call the plumber");
        phone.Ws.SetNow(call);
        phone.Ws.Sheets[0].Text = "Shopping";
        phone.Sync();

        wall += 1000;
        var pc = new Pc("pcNEWNEW", temp.Path, () => wall);
        foreach (string tip in new[] { "Fold Tack into its orb with Esc — then drag the orb anywhere", "Press Ctrl+Alt+Space in any app to open or hide Tack" })
            pc.Ws.AddTask(tip);
        var ticked = pc.Ws.AddTask("Drag the dots on the left to reorder");
        pc.Ws.Complete(ticked); // touched: it stays
        pc.Ws.AddTask("Tick this circle to finish your first task", asNow: true);
        pc.Ws.YieldFocus();
        pc.Ws.AddTask("A real task, typed before syncing");
        pc.Sync(meta => pc.Ws.LeaveWelcomeBehind(App.IsWelcomeTip));
        phone.Sync();

        Check.Equal("now=" + call.Id, Json.Views(phone.Ws).Split(' ')[0], "the phone's focus wins");
        Check.Equal("A real task, typed before syncing", string.Join(",", phone.Ws.Open.Select(t => t.Text)), "only the real task reaches the phone");
        Check.Equal("Drag the dots on the left to reorder", string.Join(",", phone.Ws.Done.Select(t => t.Text)), "a welcome task you ticked counts as yours");
        Check.Equal("Shopping", string.Join(",", pc.Ws.Sheets.Select(s => s.Text)), "the PC's blank sheet stayed behind");
    }

    private static void FindsOneDriveFolder()
    {
        using var temp = new TempDir();
        var names = new[] { "OneDriveConsumer", "OneDrive", "OneDriveCommercial" };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var n in names) Environment.SetEnvironmentVariable(n, temp.Path);
            Directory.CreateDirectory(Path.Combine(temp.Path, "Documents", "Taxes"));
            Check.Equal(null, FolderSync.FindOneDriveFolder(), "nothing yet");
            string localised = Path.Combine(temp.Path, "Aplicaciones", "Tack Focus");
            Directory.CreateDirectory(localised);
            File.WriteAllText(Path.Combine(localised, "tack-sync.json"), "{}");
            Check.Equal(localised, FolderSync.FindOneDriveFolder(), "under a localised Apps folder");
            string apps = Path.Combine(temp.Path, "Apps", "Tack Focus");
            Directory.CreateDirectory(apps);
            File.WriteAllText(Path.Combine(apps, "tack-sync.json"), "{}");
            Check.Equal(apps, FolderSync.FindOneDriveFolder(), "Apps comes first");
        }
        finally
        {
            foreach (var (n, v) in saved) Environment.SetEnvironmentVariable(n, v);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static WorkspaceData Sorted(WorkspaceData d) => new(
        d.Tasks.OrderBy(t => t.Id, StringComparer.Ordinal).ToList(),
        d.Sheets.OrderBy(s => s.Id, StringComparer.Ordinal).ToList(),
        d.Focus);

    /// <summary>The same JSON value (object keys in any order).</summary>
    private static void JsonEqual(JsonElement expected, JsonElement actual, string what)
    {
        string Canonical(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canonical)) + "]",
            JsonValueKind.Number => e.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            JsonValueKind.String => JsonSerializer.Serialize(e.GetString()),
            _ => e.GetRawText(),
        };
        Check.Equal(Canonical(expected), Canonical(actual), what);
    }

    /// <summary>A folder in memory, as the vectors describe it.</summary>
    private sealed class MemoryFolder : ISyncFolder
    {
        private int _n;
        public Dictionary<string, (string Tag, byte[] Bytes)> Files { get; } = new();

        public Task<IReadOnlyList<FolderEntry>> ListAsync() =>
            Task.FromResult<IReadOnlyList<FolderEntry>>(Files.Select(f => new FolderEntry(f.Key, f.Value.Tag)).ToList());

        public Task<byte[]> ReadAsync(FolderEntry entry) =>
            Files.TryGetValue(entry.Name, out var f) ? Task.FromResult(f.Bytes) : throw new SyncException(SyncErrorKind.Gone, "gone");

        public Task<FolderEntry> WriteAsync(string name, byte[] content)
        {
            // Numbered on from the newest file, as the cloud in Tack Focus's tests does ("t1", "t2"...).
            if (_n == 0) _n = Files.Values.Select(f => int.TryParse(f.Tag.AsSpan(1), out int k) ? k : 0).DefaultIfEmpty(0).Max();
            string tag = $"t{++_n}";
            Files[name] = (tag, content);
            return Task.FromResult(new FolderEntry(name, tag));
        }
    }

    /// <summary>A PC syncing through a folder on disk, as FolderSync does it.</summary>
    private sealed class Pc
    {
        private readonly LocalSyncFolder _folder;
        private SyncMeta _meta;

        public Pc(string id, string root, Func<long> wall)
        {
            Ws = new Workspace(new Settings(), new Hlc(id, wall), null, () => DateTimeOffset.FromUnixTimeMilliseconds(wall()));
            _folder = new LocalSyncFolder(root);
            _folder.Prepare();
            _meta = SyncMeta.Empty(root);
            Wall = wall;
        }

        public Workspace Ws { get; }

        private Func<long> Wall { get; }

        public void Sync(Action<SyncMeta>? joined = null)
        {
            var engine = new SyncEngine(Ws, _folder, new SyncHooks
            {
                Label = () => (Ws.Clock.Node, "Tack for Windows test"),
                FlushAsync = () => Task.CompletedTask,
                SaveMetaAsync = _ => Task.CompletedTask,
                Joined = joined == null ? null : () => joined(_meta),
            });
            engine.CycleAsync(_meta, Wall()).GetAwaiter().GetResult();
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("tack-sync-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
