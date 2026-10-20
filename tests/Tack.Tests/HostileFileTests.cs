using System.IO;
using System.Text;
using System.Text.Json;
using Tack.Model;
using Tack.Services;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>
/// Files in the sync folder that a broken or hostile writer made (the same cases as
/// web/tests/unit/synchostile.test.ts). Anyone who can write that folder is trusted to edit, by
/// design; but a bad file must never stop sync, swell everyone's files for good, or hide another
/// device's changes after it's gone.
/// </summary>
internal static class HostileFileTests
{
    private static readonly long Now = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private const long Day = 86_400_000;

    public static IEnumerable<(string Name, Action Run)> All()
    {
        yield return ("hostile files, M1: half a character (\\ud800) or bad UTF-8 makes a file unreadable, never an error", HalfCharacters);
        yield return ("hostile files, M1: such a file is set aside and the rest still syncs", HalfCharactersDontStopSync);
        yield return ("hostile files, M1: an import with such text says so", HalfCharactersInImport);
        yield return ("hostile files, M1: such text in this PC's own tack.json doesn't stop Tack opening", HalfCharactersAtStartup);
        yield return ("hostile files, M2: ids like \"constructor\" sync like any other", BuiltInNames);
        yield return ("hostile files, M3: one file vouching for another can't hide that device's changes", Vouching);
        yield return ("hostile files, M4: limits on records, devices seen and text", Limits);
        yield return ("hostile files, M4: only real dates, sane clocks and order keys", Fields);
        yield return ("hostile files, M4: won't write an own file every device would refuse", TooLargeToWrite);
        yield return ("hostile files, L1: never reads a file the listing shows to be too big", NeverReadsTooBig);
        yield return ("hostile files, L1: a file that fails to read costs only that file", OneFailedRead);
        yield return ("hostile files, L7: an empty \"completed\" means not done", EmptyCompleted);
        yield return ("hostile files, L8: clocks don't adopt stamps more than a year and a day ahead", ClockWindow);
    }

    // ---------------------------------------------------------------- M1

    private static void HalfCharacters()
    {
        string lone = Craft("mmmm0001", Now, text: "half \\ud83d an emoji");
        Check.Equal(DeviceFileProblem.Unreadable, ProblemOf(() => DeviceFile.Parse(Encoding.UTF8.GetBytes(lone), "mmmm0001", Now)), "lone surrogate");

        byte[] bad = Encoding.UTF8.GetBytes(Craft("mmmm0001", Now, text: "XX"));
        int at = Encoding.UTF8.GetString(bad).IndexOf("XX", StringComparison.Ordinal);
        bad[at] = 0xC3; // a two-byte sequence missing its second byte
        bad[at + 1] = 0x28;
        Check.Equal(DeviceFileProblem.Unreadable, ProblemOf(() => DeviceFile.Parse(bad, "mmmm0001", Now)), "invalid UTF-8");
    }

    private static void HalfCharactersDontStopSync()
    {
        var folder = new Folder();
        folder.Put("aaaa0001.json", Craft("aaaa0001", Now, text: "fine"));
        folder.Put("mmmm0001.json", Craft("mmmm0001", Now, text: "half \\ud800 a character"));
        var (ws, meta) = Device("desk0001");
        var result = Cycle(ws, folder, meta);
        Check.Equal("fine", string.Join(",", ws.Open.Select(t => t.Text)), "the good file merged");
        Check.True(result.Problems.Contains(("mmmm0001", DeviceFileProblem.Unreadable)), "the bad one set aside");
        Check.True(result.Wrote && folder.Files.ContainsKey("desk0001.json"), "and the PC wrote its own file");
        int reads = folder.Reads.GetValueOrDefault("mmmm0001.json");
        Cycle(ws, folder, meta);
        Check.Equal(reads, folder.Reads.GetValueOrDefault("mmmm0001.json"), "not read again until it changes");
    }

    private static void HalfCharactersInImport()
    {
        var (ws, _) = Device("desk0001");
        string file = "{\"version\":1,\"tasks\":[],\"sheets\":[],\"tackFocus\":{\"format\":1,\"tasks\":[{\"id\":\"t1\",\"text\":\"a\\ud800b\",\"created\":\"2026-10-21T09:00:00Z\",\"completed\":null,\"order\":\"V\",\"deleted\":false,"
            + $"\"clk\":{{\"text\":\"{Stamp.Encode(Now, 0, "x")}\",\"completed\":\"{Stamp.Zero}\",\"order\":\"{Stamp.Zero}\",\"deleted\":\"{Stamp.Zero}\"}}}}],\"sheets\":[]}}}}";
        var (message, undo) = FileImport.Run(ws, Encoding.UTF8.GetBytes(file), null);
        Check.Equal("This file has text Tack can't read.", message, "the import says so");
        Check.True(undo == null && ws.Open.Count == 0, "and changes nothing");
    }

    private static void HalfCharactersAtStartup()
    {
        string dir = Directory.CreateTempSubdirectory("tack-hostile-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "tack.json"),
                "{\"version\":1,\"tasks\":[{\"id\":\"a1\",\"text\":\"kept\",\"created\":\"2026-10-21T09:00:00+03:00\"}],\"sheets\":[],"
                + "\"tackFocus\":{\"format\":1,\"tasks\":[{\"id\":\"a1\",\"text\":\"x\\ud800\",\"created\":\"2026-10-21T09:00:00+03:00\",\"completed\":null,\"order\":\"V\",\"deleted\":false,"
                + $"\"clk\":{{\"text\":\"{Stamp.Encode(Now, 0, "desk")}\",\"completed\":\"{Stamp.Zero}\",\"order\":\"{Stamp.Zero}\",\"deleted\":\"{Stamp.Zero}\"}}}}],\"sheets\":[]}}}}");
            var doc = new StateStore(dir).Load();
            var data = StateFile.ToData(doc, new Hlc("desk"), Now, Now);
            Check.Equal("kept", string.Join(",", data.Tasks.Where(t => !t.Deleted).Select(t => t.Text)), "the top level is used");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---------------------------------------------------------------- M2

    private static void BuiltInNames()
    {
        var folder = new Folder();
        string seen = $"\"seen\":{{\"toString\":\"{Stamp.Encode(Now, 0, "toString")}\"}}";
        folder.Put("constructor.json", Craft("constructor", Now, text: "from constructor", extra: seen));
        var (ws, meta) = Device("desk0001");
        Cycle(ws, folder, meta);
        Check.Equal("from constructor", string.Join(",", ws.Open.Select(t => t.Text)), "merged");
        var written = DeviceFile.Parse(folder.Files["desk0001.json"].Bytes, "desk0001", Now);
        Check.True(written.Seen.ContainsKey("constructor") && written.Seen.ContainsKey("toString"), "and passed on as ordinary ids");
        Check.True(!StampSeen(Stamp.Encode(Now, 0, "constructor"), "other", new Dictionary<string, string>()), "unseen until seen");
    }

    private static bool StampSeen(string stamp, string device, Dictionary<string, string> seen) => SyncEngine.SeenBy(stamp, device, seen);

    // ---------------------------------------------------------------- M3

    private static void Vouching()
    {
        var folder = new Folder();
        long now = Now;
        folder.Put("phon0001.json", Craft("phon0001", now, text: "first"));
        // Mallory claims to have seen the phone's writes ~300 days from now, and to have written then.
        long later = now + 300 * Day;
        folder.Put("mall0001.json", Craft("mall0001", later, text: "from mallory", extra: $"\"seen\":{{\"phon0001\":\"{Stamp.Encode(later, 5, "phon0001")}\"}}"));
        var (ws, meta) = Device("desk0001", () => now);
        Cycle(ws, folder, meta, now);

        now += 1000;
        folder.Put("phon0001.json", Craft("phon0001", now, text: "second"));
        Cycle(ws, folder, meta, now);
        Check.Equal("second", ws.Open.Select(t => t.Text).Single(t => t == "second"), "the phone's next change still arrives");

        folder.Files.Remove("mall0001.json");
        now += 1000;
        folder.Put("phon0001.json", Craft("phon0001", now, text: "third"));
        Cycle(ws, folder, meta, now);
        Check.True(ws.Open.Any(t => t.Text == "third"), "and after Mallory's file is gone");

        // A claim is believed no further than the claimant's own clock.
        var folder2 = new Folder();
        folder2.Put("phon0001.json", Craft("phon0001", Now, text: "x"));
        folder2.Put("mall0001.json", Craft("mall0001", Now, text: "y", extra: $"\"seen\":{{\"phon0001\":\"{Stamp.Encode(Now + 300 * Day, 0, "phon0001")}\"}}"));
        var (ws2, meta2) = Device("desk0002");
        Cycle(ws2, folder2, meta2);
        Check.True(Stamp.Time(meta2.Seen["phon0001"]) <= Now, "the claim is cut back to Mallory's own clock");
    }

    // ---------------------------------------------------------------- M4

    private static void Limits()
    {
        string tooMany = Craft("mmmm0001", Now, text: "x", tasksJson: "[" + string.Join(",", Enumerable.Repeat("{}", DeviceFile.MaxRecords + 1)) + "]");
        Check.Equal(DeviceFileProblem.Unreadable, ProblemOf(() => DeviceFile.Parse(Encoding.UTF8.GetBytes(tooMany), "mmmm0001", Now)), "too many records");
        string atLimit = Craft("mmmm0001", Now, text: "x", tasksJson: "[" + string.Join(",", Enumerable.Repeat("{}", DeviceFile.MaxRecords)) + "]");
        DeviceFile.Parse(Encoding.UTF8.GetBytes(atLimit), "mmmm0001", Now); // fine at the limit

        string seen = "\"seen\":{" + string.Join(",", Enumerable.Range(0, DeviceFile.MaxSeen + 1).Select(i => $"\"dev{i:D5}\":\"{Stamp.Encode(Now, 0, "x")}\"")) + "}";
        Check.Equal(DeviceFileProblem.Unreadable, ProblemOf(() => DeviceFile.Parse(Encoding.UTF8.GetBytes(Craft("mmmm0001", Now, text: "x", extra: seen)), "mmmm0001", Now)), "too many devices seen");

        string longText = Craft("mmmm0001", Now, text: new string('x', DeviceFile.MaxTaskText + 1));
        Check.Equal(0, DeviceFile.Parse(Encoding.UTF8.GetBytes(longText), "mmmm0001", Now).Tasks.Count, "an over-long task is skipped");
    }

    private static void Fields()
    {
        Check.True(Stamp.IsStamp(Stamp.Encode(Now, 0, new string('x', 32))), "a 32-character device part");
        Check.True(!Stamp.IsStamp(Stamp.Encode(Now, 0, new string('x', 33))), "no 33-character one");
        Check.True(OrderKey.IsOrderKey(new string('V', OrderKey.MaxLength)) && !OrderKey.IsOrderKey(new string('V', OrderKey.MaxLength + 1)), "order keys of a sane length");

        var odd = Record(created: "!", completed: null);
        Check.True(odd.Created != "!" && TackFile.IsFileDate(odd.Created), "an odd created date counts as missing");
        Check.True(Record(created: new string('x', 100_000), completed: null).Created.Length < 40, "and a huge one");
        Check.Equal("2026-10-21T09:00:00.1234567+03:00", Record(created: "2026-10-21T09:00:00.1234567+03:00", completed: null).Created, "real dates are kept, .NET's seven digits too");
        Check.True(!TackFile.IsFileDate("2026-10-21T09:00:00.000+03:00 "), "nothing after the date");
    }

    private static void TooLargeToWrite()
    {
        var folder = new Folder();
        var (ws, meta) = Device("desk0001");
        ws.AddSheet().Text = new string('a', DeviceFile.MaxBytes + 1000);
        SyncErrorKind? kind = null;
        try
        {
            Cycle(ws, folder, meta);
        }
        catch (SyncException ex)
        {
            kind = ex.Kind;
        }
        Check.Equal<SyncErrorKind?>(SyncErrorKind.TooLarge, kind, "refused as too large");
        Check.True(!folder.Files.ContainsKey("desk0001.json") && meta.Own.Pending == null, "nothing written or pending");
    }

    // ---------------------------------------------------------------- L1

    private static void NeverReadsTooBig()
    {
        var folder = new Folder();
        folder.Put("bigbig01.json", "x", size: DeviceFile.MaxBytes + 1L);
        var (ws, meta) = Device("desk0001");
        var result = Cycle(ws, folder, meta);
        Check.True(result.Problems.Contains(("bigbig01", DeviceFileProblem.Unreadable)), "set aside");
        Check.Equal(0, folder.Reads.GetValueOrDefault("bigbig01.json"), "without being read");
    }

    private static void OneFailedRead()
    {
        var folder = new Folder();
        folder.Put("aaaa0001.json", Craft("aaaa0001", Now, text: "from ann"));
        folder.Put("bbbb0001.json", Craft("bbbb0001", Now, text: "from bob"));
        folder.FailNextRead = SyncErrorKind.Offline; // the OneDrive app holds the first file
        var (ws, meta) = Device("desk0001");
        var first = Cycle(ws, folder, meta);
        Check.True(first.Merged.Count == 1 && first.Wrote, "the other file merged and the PC wrote its own");
        var second = Cycle(ws, folder, meta);
        Check.Equal("from ann,from bob", string.Join(",", ws.Open.Select(t => t.Text).Order()), "the failed one comes next time");
        Check.Equal(2, first.Merged.Count + second.Merged.Count, "each merged once");
    }

    // ---------------------------------------------------------------- L7, L8

    private static void EmptyCompleted()
    {
        Check.Equal<string?>(null, Record(created: "2026-10-21T09:00:00Z", completed: "").Completed, "empty");
        Check.Equal<string?>(null, Record(created: "2026-10-21T09:00:00Z", completed: "not a date").Completed, "not a date");
        Check.Equal<string?>("2026-10-21T10:00:00Z", Record(created: "2026-10-21T09:00:00Z", completed: "2026-10-21T10:00:00Z").Completed, "a date");
    }

    private static void ClockWindow()
    {
        var h = new Hlc("aaaa1111", () => Now);
        h.Observe(Stamp.Encode(Now + Stamp.MaxAheadMs + 1, 0, "bbbb2222"));
        Check.Equal((double)Now, Stamp.Time(h.Now()), "not adopted");
        h.Observe(Stamp.Encode(Now + 30 * Day, 0, "bbbb2222"));
        Check.Equal((double)(Now + 30 * Day), Stamp.Time(h.Now()), "nearer stamps are");
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A hand-made device file with one live task (its text as JSON-escaped source).</summary>
    private static string Craft(string device, long now, string text, string? extra = null, string? tasksJson = null)
    {
        string at = Stamp.Encode(now, 0, device);
        string tasks = tasksJson ?? $"[{{\"id\":\"t{device}\",\"text\":\"{text}\",\"created\":\"2026-10-21T09:00:00.000Z\",\"completed\":null,\"order\":\"V\",\"deleted\":false,"
            + $"\"clk\":{{\"text\":\"{at}\",\"completed\":\"{Stamp.Zero}\",\"order\":\"{at}\",\"deleted\":\"{Stamp.Zero}\"}}}}]";
        return $"{{\"format\":\"tack-sync-device\",\"version\":1,\"device\":\"{device}\",\"name\":\"crafted\",\"app\":\"test\",\"written\":\"{Stamp.Encode(now, 1, device)}\","
            + (extra ?? "\"seen\":{}") + $",\"focus\":{{\"id\":null,\"clk\":\"{Stamp.Zero}\"}},\"tasks\":{tasks},\"sheets\":[],\"deleted\":{{\"tasks\":{{}},\"sheets\":{{}}}}}}";
    }

    private static TaskData Record(string created, string? completed)
    {
        string at = Stamp.Encode(Now, 0, "aaaa1111");
        string json = $"{{\"id\":\"t1\",\"text\":\"x\",\"created\":{JsonSerializer.Serialize(created)},\"completed\":{JsonSerializer.Serialize(completed)},\"order\":\"V\",\"deleted\":false,"
            + $"\"clk\":{{\"text\":\"{at}\",\"completed\":\"{at}\",\"order\":\"{at}\",\"deleted\":\"{Stamp.Zero}\"}}}}";
        using var doc = JsonDocument.Parse(json);
        return TackFile.ReadTaskRecord(doc.RootElement, Now, clean: true)!;
    }

    private static DeviceFileProblem ProblemOf(Action parse)
    {
        try
        {
            parse();
        }
        catch (DeviceFileException ex)
        {
            return ex.Problem;
        }
        throw new InvalidOperationException("expected the file to be refused");
    }

    private static (Workspace Ws, SyncMeta Meta) Device(string id, Func<long>? wall = null)
    {
        var clock = wall ?? (() => Now);
        return (new Workspace(new Settings(), new Hlc(id, clock), null, () => DateTimeOffset.FromUnixTimeMilliseconds(clock())), SyncMeta.Empty("folder"));
    }

    private static CycleResult Cycle(Workspace ws, Folder folder, SyncMeta meta, long? now = null)
    {
        var engine = new SyncEngine(ws, folder, new SyncHooks
        {
            Label = () => (ws.Clock.Node, "test"),
            FlushAsync = () => Task.CompletedTask,
            SaveMetaAsync = _ => Task.CompletedTask,
        });
        return engine.CycleAsync(meta, now ?? Now).GetAwaiter().GetResult();
    }

    /// <summary>A folder in memory that reports sizes, counts reads and can fail one.</summary>
    private sealed class Folder : ISyncFolder
    {
        private int _n;
        public Dictionary<string, (string Tag, byte[] Bytes, long Size)> Files { get; } = new();
        public Dictionary<string, int> Reads { get; } = new();
        public SyncErrorKind? FailNextRead { get; set; }

        public void Put(string name, string text, long? size = null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            Files[name] = ($"t{++_n}", bytes, size ?? bytes.Length);
        }

        public Task<IReadOnlyList<FolderEntry>> ListAsync() =>
            Task.FromResult<IReadOnlyList<FolderEntry>>(Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new FolderEntry(f.Key, f.Value.Tag, f.Value.Size)).ToList());

        public Task<byte[]> ReadAsync(FolderEntry entry)
        {
            if (FailNextRead is { } kind)
            {
                FailNextRead = null;
                throw new SyncException(kind, "can't read it right now");
            }
            Reads[entry.Name] = Reads.GetValueOrDefault(entry.Name) + 1;
            return Files.TryGetValue(entry.Name, out var f) ? Task.FromResult(f.Bytes) : throw new SyncException(SyncErrorKind.Gone, "gone");
        }

        public Task<FolderEntry> WriteAsync(string name, byte[] content)
        {
            Files[name] = ($"t{++_n}", content, content.Length);
            return Task.FromResult(new FolderEntry(name, Files[name].Tag, content.Length));
        }
    }
}
