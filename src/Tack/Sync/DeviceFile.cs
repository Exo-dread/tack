using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Tack.Sync;

/// <summary>
/// A device file (port of web/src/sync/devicefile.ts): everything one device knows, written only
/// by that device, read by all the others. The sync folder holds one per device
/// (<c>devices/&lt;device id&gt;.json</c>), so devices never write the same file.
///
///   format, version   what this is (a newer, incompatible Tack would bump "version")
///   device            the writer's id: the same id its clock puts in every stamp
///   name, app         for people: "Tack for Windows on DESKTOP-1", "Tack for Windows 1.3.0"
///   written           the writer's clock when it wrote the file (later than every stamp inside)
///   seen              for each other device, the "written" of the latest version of its file
///                     merged into this one (directly, or through a third device's file)
///   focus, tasks, sheets   live records, exactly as in tack.json's tackFocus section (a task's or
///                     sheet's "space" only once it has been put in one)
///   spaces            live spaces, only if there are any
///   deleted           deleted records, as id → the clock of the deletion ("spaces" only if any
///                     space was deleted). Kept for good while sync is on, so an out-of-date copy
///                     can never bring a deleted task back.
/// With no spaces and nothing put in one, the file is exactly what Tack 1.3 writes.
/// </summary>
internal sealed record DeviceFile(
    int Version,
    string Device,
    string Name,
    string App,
    string Written,
    IReadOnlyDictionary<string, string> Seen,
    FocusData Focus,
    IReadOnlyList<TaskData> Tasks,
    IReadOnlyList<SheetData> Sheets,
    IReadOnlyList<SpaceData> Spaces,
    bool SeenOver = false)
{
    public const string Format = "tack-sync-device";
    public const int CurrentVersion = 1;
    /// <summary>Device files bigger than this can't be Tack's.</summary>
    public const int MaxBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Limits inside a device file (the same as devicefile.ts and limits.ts), far beyond any real
    /// use. A file over them is refused whole and an over-long record is skipped, so a damaged or
    /// crafted file can't swell every device's own file for good. (A space name is the one thing cut
    /// rather than skipped: every device keeps names to <see cref="MaxSpaceName"/> wherever they come
    /// from, so no device can hold a name its peers would refuse.) A file with more "seen" entries than
    /// <see cref="MaxSeen"/> isn't refused: it's marked <see cref="SeenOver"/> (see <see cref="TrimSeen"/>).
    /// </summary>
    public const int MaxRecords = 200_000; // tasks + sheets + spaces + deletions
    public const int MaxSeen = 1_000;
    public const int MaxTaskText = 100_000;
    public const int MaxNoteText = 1_000_000;
    public const int MaxSpaceName = SpaceField.MaxName;

    /// <summary>
    /// Records a device holds before it takes no more in bulk from elsewhere (other devices' files,
    /// imports): the rest is room for its own edits and deletions, so its own file stays within
    /// <see cref="MaxRecords"/>. New records one device file may still bring past that (a peer's
    /// everyday edits), up to <see cref="MaxRecords"/>: so devices that share a lot keep syncing, while
    /// a file that would add more at once (a whole workspace, a flood) is held to <see cref="MaxTaken"/>.
    /// Live spaces a device shows before it takes no more from elsewhere: every one is a button on
    /// screen. Lines one paste (or one "Make task" from a note) turns into tasks. (limits.ts)
    /// </summary>
    public const int MaxTaken = MaxRecords - 20_000;
    public const int MaxTrickle = 2_000;
    public const int MaxSpaces = 1_000;
    public const int MaxPasteLines = 10_000;

    /// <summary>
    /// Keeps this device's "seen" (changed in place) within <see cref="MaxSeen"/> before it goes into
    /// its file, so every reader takes the file: only a crafted file's made-up claims, passed on, ever
    /// make it longer. Devices with a file in the folder (<paramref name="present"/>: file names) stay
    /// first, then the latest claims. Dropping a claim costs little: at worst a note edited on both
    /// sides is kept twice. Tack Focus trims the same way (trimSeen in devicefile.ts).
    /// </summary>
    public static void TrimSeen(Dictionary<string, string> seen, string me, IReadOnlySet<string> present)
    {
        var ids = seen.Keys.Where(d => d != me && IsDeviceId(d)).ToList();
        if (ids.Count <= MaxSeen) return;
        foreach (string d in ids.OrderByDescending(d => present.Contains(FileName(d)))
                     .ThenByDescending(d => seen[d], StringComparer.Ordinal)
                     .ThenBy(d => d, StringComparer.Ordinal)
                     .Skip(MaxSeen)
                     .ToList())
            seen.Remove(d);
    }

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool IsDeviceId(string? id) =>
        id is { Length: >= 4 and <= 32 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'));

    public static string FileName(string device) => device + ".json";

    /// <summary>The device a file in the folder belongs to, or null for anything else (sync clients' copies, temp files).</summary>
    public static string? DeviceOfFile(string name) =>
        name.EndsWith(".json", StringComparison.Ordinal) && name[..^5] is var id && IsDeviceId(id) ? id : null;

    /// <summary>A deleted task as a device file carries it: just enough to win against any older copy.</summary>
    public static TaskData DeadTask(string id, string deleted) =>
        new(id, "", "", null, OrderKey.Mid, true, "", new TaskClocks(Stamp.Zero, Stamp.Zero, Stamp.Zero, deleted, Stamp.Zero));

    public static SheetData DeadSheet(string id, string deleted) =>
        new(id, "", "", OrderKey.Mid, true, "", new SheetClocks(Stamp.Zero, Stamp.Zero, deleted, Stamp.Zero));

    public static SpaceData DeadSpace(string id, string deleted) =>
        new(id, "", OrderKey.Mid, true, new SpaceClocks(Stamp.Zero, Stamp.Zero, deleted));

    /// <summary>Writes a device file, key for key as Tack Focus writes it.</summary>
    public static byte[] Write(WorkspaceData data, string device, string name, string app, string written, IReadOnlyDictionary<string, string> seen)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteNumber("version", CurrentVersion);
            w.WriteString("device", device);
            w.WriteString("name", name);
            w.WriteString("app", app);
            w.WriteString("written", written);
            w.WriteStartObject("seen");
            foreach (var (d, stamp) in seen.Where(e => e.Key != device && IsDeviceId(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal))
                w.WriteString(d, stamp);
            w.WriteEndObject();
            w.WriteStartObject("focus");
            w.WriteString("id", data.Focus.Id);
            w.WriteString("clk", data.Focus.Clk);
            w.WriteEndObject();

            w.WriteStartArray("tasks");
            foreach (var t in data.Tasks.Where(t => !t.Deleted).OrderBy(t => t.Id, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("id", t.Id);
                w.WriteString("text", t.Text);
                w.WriteString("created", t.Created);
                w.WriteString("completed", t.Completed);
                w.WriteString("order", t.Order);
                w.WriteBoolean("deleted", false);
                if (t.Clk.Space != Stamp.Zero) w.WriteString("space", t.Space);
                w.WriteStartObject("clk");
                w.WriteString("text", t.Clk.Text);
                w.WriteString("completed", t.Clk.Completed);
                w.WriteString("order", t.Clk.Order);
                w.WriteString("deleted", t.Clk.Deleted);
                if (t.Clk.Space != Stamp.Zero) w.WriteString("space", t.Clk.Space);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("sheets");
            foreach (var s in data.Sheets.Where(s => !s.Deleted).OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("text", s.Text);
                w.WriteString("updated", s.Updated);
                w.WriteString("order", s.Order);
                w.WriteBoolean("deleted", false);
                if (s.Clk.Space != Stamp.Zero) w.WriteString("space", s.Space);
                w.WriteStartObject("clk");
                w.WriteString("text", s.Clk.Text);
                w.WriteString("order", s.Clk.Order);
                w.WriteString("deleted", s.Clk.Deleted);
                if (s.Clk.Space != Stamp.Zero) w.WriteString("space", s.Clk.Space);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            var liveSpaces = data.Spaces.Where(s => !s.Deleted).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
            if (liveSpaces.Count > 0)
            {
                w.WriteStartArray("spaces");
                foreach (var s in liveSpaces)
                {
                    w.WriteStartObject();
                    w.WriteString("id", s.Id);
                    w.WriteString("name", s.Name);
                    w.WriteString("order", s.Order);
                    w.WriteBoolean("deleted", false);
                    w.WriteStartObject("clk");
                    w.WriteString("name", s.Clk.Name);
                    w.WriteString("order", s.Clk.Order);
                    w.WriteString("deleted", s.Clk.Deleted);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            w.WriteStartObject("deleted");
            w.WriteStartObject("tasks");
            foreach (var t in data.Tasks.Where(t => t.Deleted).OrderBy(t => t.Id, StringComparer.Ordinal)) w.WriteString(t.Id, t.Clk.Deleted);
            w.WriteEndObject();
            w.WriteStartObject("sheets");
            foreach (var s in data.Sheets.Where(s => s.Deleted).OrderBy(s => s.Id, StringComparer.Ordinal)) w.WriteString(s.Id, s.Clk.Deleted);
            w.WriteEndObject();
            if (data.Spaces.Any(s => s.Deleted))
            {
                w.WriteStartObject("spaces");
                foreach (var s in data.Spaces.Where(s => s.Deleted).OrderBy(s => s.Id, StringComparer.Ordinal)) w.WriteString(s.Id, s.Clk.Deleted);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Reads a device file, checking everything: a damaged or hand-edited file costs that file's
    /// records at worst, never the rest. <paramref name="device"/> is the id its name says it belongs to.
    /// </summary>
    public static DeviceFile Parse(ReadOnlyMemory<byte> bytes, string device, long nowMs)
    {
        if (bytes.Length > MaxBytes) throw new DeviceFileException(DeviceFileProblem.Unreadable);
        if (bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) bytes = bytes[3..];
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw new DeviceFileException(DeviceFileProblem.Unreadable);
        }
        try
        {
            return Read(json, device, nowMs);
        }
        catch (Exception ex) when (TackFile.IsUnreadableText(ex))
        {
            // Valid JSON with text that can't be read (invalid UTF-8, a lone surrogate): this file
            // is unreadable, like a damaged one, and is set aside; it never stops the sync.
            throw new DeviceFileException(DeviceFileProblem.Unreadable);
        }
    }

    private static DeviceFile Read(JsonDocument json, string device, long nowMs)
    {
        using (json)
        {
            var doc = json.RootElement;
            if (doc.ValueKind != JsonValueKind.Object || Str(doc, "format") != Format
                || !doc.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number
                || v.GetDouble() is var version && (Math.Floor(version) != version || version < 1))
                throw new DeviceFileException(DeviceFileProblem.Unreadable);
            if (version > CurrentVersion) throw new DeviceFileException(DeviceFileProblem.Newer);
            if (Str(doc, "device") != device || !IsDeviceId(device)) throw new DeviceFileException(DeviceFileProblem.Mismatch);
            string? written = Str(doc, "written");
            if (!Stamp.IsPlausible(written, nowMs) || !IsArray(doc, "tasks") || !IsArray(doc, "sheets"))
                throw new DeviceFileException(DeviceFileProblem.Unreadable);
            doc.TryGetProperty("deleted", out var deletedMaps);
            // Spaces are optional (Tack 1.3 writes none); anything but an array counts as none.
            int spaceCount = IsArray(doc, "spaces") ? doc.GetProperty("spaces").GetArrayLength() : 0;
            if (doc.GetProperty("tasks").GetArrayLength() + doc.GetProperty("sheets").GetArrayLength() + spaceCount
                + Count(deletedMaps, "tasks") + Count(deletedMaps, "sheets") + Count(deletedMaps, "spaces") > MaxRecords)
                throw new DeviceFileException(DeviceFileProblem.Unreadable);
            // More "seen" claims than any device writes: the file is still read, and only its claims
            // about devices that matter count (SyncEngine). Versions before TrimSeen passed a crafted
            // file's claims on, and refusing their files would stop sync between them for good.
            bool seenOver = Count(doc, "seen") > MaxSeen;

            // Duplicate ids (only a hand edit makes them) merge rather than one silently replacing the other.
            var tasks = new Dictionary<string, TaskData>();
            void AddTask(TaskData? t)
            {
                if (t != null && t.Text.Length <= MaxTaskText) tasks[t.Id] = tasks.TryGetValue(t.Id, out var had) ? Merge.Task(had, t).Value : t;
            }
            var sheets = new Dictionary<string, SheetData>();
            void AddSheet(SheetData? s)
            {
                if (s != null && s.Text.Length <= MaxNoteText) sheets[s.Id] = sheets.TryGetValue(s.Id, out var had) ? Merge.Sheet(had, s).Value : s;
            }
            // Names arrive cut to MaxSpaceName (TackFile.ReadSpaceRecord), as every device keeps them.
            var spaces = new Dictionary<string, SpaceData>();
            void AddSpace(SpaceData? s)
            {
                if (s != null) spaces[s.Id] = spaces.TryGetValue(s.Id, out var had) ? Merge.Space(had, s).Value : s;
            }
            foreach (var r in doc.GetProperty("tasks").EnumerateArray()) AddTask(TackFile.ReadTaskRecord(r, nowMs, clean: true));
            foreach (var r in doc.GetProperty("sheets").EnumerateArray()) AddSheet(TackFile.ReadSheetRecord(r, nowMs));
            if (spaceCount > 0)
            {
                foreach (var r in doc.GetProperty("spaces").EnumerateArray()) AddSpace(TackFile.ReadSpaceRecord(r, nowMs));
            }
            if (doc.TryGetProperty("deleted", out var deleted) && deleted.ValueKind == JsonValueKind.Object)
            {
                foreach (var (id, stamp) in Entries(deleted, "tasks", nowMs)) AddTask(DeadTask(id, stamp));
                foreach (var (id, stamp) in Entries(deleted, "sheets", nowMs)) AddSheet(DeadSheet(id, stamp));
                foreach (var (id, stamp) in Entries(deleted, "spaces", nowMs)) AddSpace(DeadSpace(id, stamp));
            }

            var seen = new Dictionary<string, string>();
            foreach (var (d, stamp) in Entries(doc, "seen", nowMs)) if (IsDeviceId(d)) seen[d] = stamp;
            return new DeviceFile(
                (int)version,
                device,
                Cap(Str(doc, "name")),
                Cap(Str(doc, "app")),
                written!,
                seen,
                doc.TryGetProperty("focus", out var f) ? TackFile.ReadFocus(f, nowMs) : FocusData.None,
                tasks.Values.ToList(),
                sheets.Values.ToList(),
                spaces.Values.ToList(),
                seenOver);
        }
    }

    /// <summary>Well-formed <c>id: clock</c> pairs of a JSON object property (anything else is skipped).</summary>
    private static IEnumerable<(string Id, string Stamp)> Entries(JsonElement obj, string name, long nowMs)
    {
        if (!obj.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object) yield break;
        foreach (var p in map.EnumerateObject())
        {
            if (Ids.IsValid(p.Name) && p.Value.ValueKind == JsonValueKind.String && Stamp.IsPlausible(p.Value.GetString(), nowMs))
                yield return (p.Name, p.Value.GetString()!);
        }
    }

    /// <summary>How many properties an object property has (0 if it's missing or not an object).</summary>
    private static int Count(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var map) && map.ValueKind == JsonValueKind.Object
            ? map.EnumerateObject().Count() : 0;

    private static string Cap(string? s) => s == null ? "" : s.Length > 100 ? s[..100] : s;

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsArray(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array;
}

/// <summary>
/// Why a device file isn't used. <c>Full</c> is never thrown by the reader: the engine sets a file
/// aside so when taking it in would leave this PC holding more than it takes from elsewhere.
/// </summary>
internal enum DeviceFileProblem { Unreadable, Newer, Mismatch, Full }

internal sealed class DeviceFileException(DeviceFileProblem problem) : Exception(problem switch
{
    DeviceFileProblem.Newer => "Written by a newer version of Tack",
    DeviceFileProblem.Mismatch => "Device id does not match the file name",
    DeviceFileProblem.Full => "Holds more than this PC takes in",
    _ => "Not a readable Tack device file",
})
{
    public DeviceFileProblem Problem { get; } = problem;
}
