using System.Text.Json;

namespace Tack.Sync;

/// <summary>Raised for files that can't be imported; the message is meant for the user.</summary>
internal sealed class TackFileException(string message) : Exception(message);

/// <summary>What reading a file needs to know about the workspace it is going into.</summary>
internal interface IImportTarget
{
    Hlc Clock { get; }

    /// <summary>The focus register's task id (which may be finished or deleted).</summary>
    string? FocusId { get; }

    /// <summary>The order key of the top open task (excluding the one in focus).</summary>
    string? FirstOpenOrder { get; }

    /// <summary>The order key of the last sheet.</summary>
    string? LastSheetOrder { get; }

    /// <summary>Any task with this id, deleted ones included.</summary>
    TaskData? FindTask(string id);

    SheetData? FindSheet(string id);

    /// <summary>Every task, sheet and space held, deleted ones included (as a device file counts them).</summary>
    int RecordCount { get; }

    /// <summary>
    /// How many records taking <paramref name="data"/> in would add, and how many spaces it would
    /// show that don't show now (new live ones, deleted ones brought back).
    /// </summary>
    (int Records, int Spaces) WouldAdd(WorkspaceData data);
}

/// <summary>
/// tack.json, shared by both apps (port of web/src/model/file.ts). The top level (version, tasks,
/// sheets) is what Tack for Windows has always written; the "tackFocus" section carries what
/// multi-device merging needs (clocks, sort keys, deletions, the focus register). Imports accept
/// either flavour.
/// </summary>
internal static partial class TackFile
{
    public const int Format = 1;

    /// <summary>People edit tack.json by hand, so comments and trailing commas are fine.</summary>
    public static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parses a file's bytes (a leading BOM is fine).</summary>
    public static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) bytes = bytes[3..];
        try
        {
            return JsonDocument.Parse(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            throw new TackFileException("This file isn't valid JSON.");
        }
    }

    /// <summary>
    /// Imports a file: anything new is added; for anything edited in both places, the newer edit
    /// wins; and a note whose text differs is kept twice rather than overwritten.
    /// </summary>
    /// <param name="lastModified">When the file was last saved (ms), to settle edits made in both places.</param>
    public static (WorkspaceData Data, string? Base) Read(JsonElement doc, IImportTarget ws, long? lastModified, long nowMs)
    {
        if (doc.ValueKind != JsonValueKind.Object) throw new TackFileException("This doesn't look like a Tack file.");
        try
        {
            if (doc.TryGetProperty("tackFocus", out var extra) && extra.ValueKind == JsonValueKind.Object && IsArray(extra, "tasks") && IsArray(extra, "sheets"))
            {
                // A file with more records than any device may hold is refused before reading them.
                if (Length(extra, "tasks") + Length(extra, "sheets") + Length(extra, "spaces") > DeviceFile.MaxRecords) throw new TackFileException(TooMany);
                if (ReadSection(extra, nowMs, clean: true) is { } data)
                {
                    // Text longer than any device reads is skipped, as in device files (on import only:
                    // this PC's own tack.json keeps whatever it has).
                    data = data with
                    {
                        Tasks = data.Tasks.Where(t => t.Text.Length <= DeviceFile.MaxTaskText).ToList(),
                        Sheets = data.Sheets.Where(s => s.Text.Length <= DeviceFile.MaxNoteText).ToList(),
                    };
                    // Files with clocks merge field by field; for notes, a differing text is kept as a copy
                    // beside the newer one (base Zero: treat every difference as concurrent).
                    return (WithinLimits(data, ws), Stamp.Zero);
                }
            }

            if (!IsArray(doc, "tasks") && !IsArray(doc, "sheets")) throw new TackFileException("This doesn't look like a Tack file.");
            if (Length(doc, "tasks") + Length(doc, "sheets") > DeviceFile.MaxRecords) throw new TackFileException(TooMany);
            return (WithinLimits(ReadDesktop(doc, ws, lastModified), ws), null);
        }
        catch (Exception ex) when (IsUnreadableText(ex))
        {
            throw new TackFileException("This file has text Tack can't read.");
        }
    }

    private const string TooMany = "This file holds more tasks and notes than Tack can keep in sync.";

    /// <summary>
    /// What an import would add, if this PC can take it: no more records in all than a device takes
    /// from elsewhere (<see cref="DeviceFile.MaxTaken"/>, deleted ones included), so its sync file stays
    /// one every device reads, Undo included (Undo keeps what it removes as deletions while syncing).
    /// </summary>
    private static WorkspaceData WithinLimits(WorkspaceData data, IImportTarget ws)
    {
        var (added, _) = ws.WouldAdd(data);
        if (added > 0 && ws.RecordCount + added > DeviceFile.MaxTaken) throw new TackFileException(TooMany);
        return data;
    }

    /// <summary>
    /// What reading valid JSON can still throw: invalid UTF-8 inside a string, or a lone surrogate
    /// ("\ud800"), which JSON allows but .NET strings can't be read from. Such a file is unreadable,
    /// never a reason to stop.
    /// </summary>
    public static bool IsUnreadableText(Exception ex) => ex is InvalidOperationException or FormatException or ArgumentException;

    // ------------------------------------------------------------------ the tackFocus section

    /// <summary>
    /// The records of a tackFocus section, each checked on its own: anything malformed is skipped,
    /// never the whole file. Null if there is no usable section.
    /// </summary>
    /// <param name="clean">Collapse white space in task text, as Tack Focus does on import.</param>
    public static WorkspaceData? ReadSection(JsonElement extra, long nowMs, bool clean)
    {
        if (extra.ValueKind != JsonValueKind.Object || !IsArray(extra, "tasks") || !IsArray(extra, "sheets")) return null;
        var tasks = new List<TaskData>();
        foreach (var r in extra.GetProperty("tasks").EnumerateArray())
            if (ReadTaskRecord(r, nowMs, clean) is { } t) tasks.Add(t);
        var sheets = new List<SheetData>();
        foreach (var r in extra.GetProperty("sheets").EnumerateArray())
            if (ReadSheetRecord(r, nowMs) is { } s) sheets.Add(s);
        var focus = extra.TryGetProperty("focus", out var f) ? ReadFocus(f, nowMs) : FocusData.None;
        // Spaces are optional (only written once there are any).
        var spaces = new List<SpaceData>();
        if (IsArray(extra, "spaces"))
        {
            foreach (var r in extra.GetProperty("spaces").EnumerateArray())
                if (ReadSpaceRecord(r, nowMs) is { } s) spaces.Add(s);
        }
        return new WorkspaceData(tasks, sheets, focus, spaces);
    }

    /// <summary>The device id this file's own app stamps with (only Tack for Windows writes one).</summary>
    public static string? ReadDevice(JsonElement extra) =>
        extra.ValueKind == JsonValueKind.Object && Str(extra, "device") is { Length: > 0 and <= 32 } d && IsDeviceId(d) ? d : null;

    public static TaskData? ReadTaskRecord(JsonElement r, long nowMs, bool clean)
    {
        if (r.ValueKind != JsonValueKind.Object || Str(r, "id") is not { } id || !Ids.IsValid(id)) return null;
        if (!r.TryGetProperty("clk", out var clk) || clk.ValueKind != JsonValueKind.Object) return null;
        string? ct = Str(clk, "text"), cc = Str(clk, "completed"), co = Str(clk, "order"), cd = Str(clk, "deleted");
        if (!Stamp.IsPlausible(ct, nowMs) || !Stamp.IsPlausible(cc, nowMs) || !Stamp.IsPlausible(co, nowMs) || !Stamp.IsPlausible(cd, nowMs)) return null;
        string text = Str(r, "text") ?? "";
        if (clean) text = JsText.CleanTaskText(text);
        bool deleted = IsTrue(r, "deleted");
        if (text.Length == 0 && !deleted) return null;
        string? order = Str(r, "order");
        string? created = Str(r, "created"), completed = Str(r, "completed");
        var (space, cs) = SpaceField.Read(Str(r, "space"), Str(clk, "space"), nowMs);
        return new TaskData(
            id,
            text,
            IsFileDate(created) ? created! : EpochText(),
            // Anything but a date (an empty string included) means not done, as in Tack Focus.
            IsFileDate(completed) ? completed : null,
            OrderKey.IsOrderKey(order) ? order! : OrderKey.Mid,
            deleted,
            space,
            new TaskClocks(ct!, cc!, co!, cd!, cs));
    }

    public static SheetData? ReadSheetRecord(JsonElement r, long nowMs)
    {
        if (r.ValueKind != JsonValueKind.Object || Str(r, "id") is not { } id || !Ids.IsValid(id)) return null;
        if (!r.TryGetProperty("clk", out var clk) || clk.ValueKind != JsonValueKind.Object) return null;
        string? ct = Str(clk, "text"), co = Str(clk, "order"), cd = Str(clk, "deleted");
        if (!Stamp.IsPlausible(ct, nowMs) || !Stamp.IsPlausible(co, nowMs) || !Stamp.IsPlausible(cd, nowMs)) return null;
        string? order = Str(r, "order"), updated = Str(r, "updated");
        var (space, cs) = SpaceField.Read(Str(r, "space"), Str(clk, "space"), nowMs);
        return new SheetData(
            id,
            JsText.NoteText(Str(r, "text") ?? ""),
            IsFileDate(updated) ? updated! : EpochText(),
            OrderKey.IsOrderKey(order) ? order! : OrderKey.Mid,
            IsTrue(r, "deleted"),
            space,
            new SheetClocks(ct!, co!, cd!, cs));
    }

    /// <summary>
    /// A space record (readSpaceRecord in file.ts): its name is one line, like a task's, and at most
    /// 100 characters (<see cref="SpaceField.CleanName"/>); an empty name is fine.
    /// </summary>
    public static SpaceData? ReadSpaceRecord(JsonElement r, long nowMs)
    {
        if (r.ValueKind != JsonValueKind.Object || Str(r, "id") is not { } id || !Ids.IsValid(id)) return null;
        if (!r.TryGetProperty("clk", out var clk) || clk.ValueKind != JsonValueKind.Object) return null;
        string? cn = Str(clk, "name"), co = Str(clk, "order"), cd = Str(clk, "deleted");
        if (!Stamp.IsPlausible(cn, nowMs) || !Stamp.IsPlausible(co, nowMs) || !Stamp.IsPlausible(cd, nowMs)) return null;
        string? order = Str(r, "order");
        return new SpaceData(
            id,
            SpaceField.CleanName(Str(r, "name") ?? ""),
            OrderKey.IsOrderKey(order) ? order! : OrderKey.Mid,
            IsTrue(r, "deleted"),
            new SpaceClocks(cn!, co!, cd!));
    }

    public static FocusData ReadFocus(JsonElement r, long nowMs)
    {
        if (r.ValueKind != JsonValueKind.Object || Str(r, "clk") is not { } clk || !Stamp.IsPlausible(clk, nowMs)) return FocusData.None;
        if (!r.TryGetProperty("id", out var id)) return FocusData.None;
        if (id.ValueKind == JsonValueKind.Null) return new FocusData(null, clk);
        if (id.ValueKind == JsonValueKind.String && Ids.IsValid(id.GetString())) return new FocusData(id.GetString(), clk);
        return FocusData.None;
    }

    /// <summary>
    /// Writes a tackFocus section, field for field as Tack Focus's export writes it. Deleted records
    /// travel so the deletion can be merged elsewhere, but without their text (a deleted space
    /// without its name, and without its name's clock: a blank name with the real name's clock
    /// would tie with that name when the deletion is undone, and could win). A record's "space"
    /// is written only once it has been put in one, and "spaces" only if there are any, so without
    /// spaces the section is exactly what Tack 1.3 writes.
    /// </summary>
    public static void WriteSection(Utf8JsonWriter w, WorkspaceData data, string exported, string? device)
    {
        w.WriteStartObject();
        w.WriteNumber("format", Format);
        w.WriteString("exported", exported);
        if (device != null) w.WriteString("device", device);
        w.WritePropertyName("focus");
        w.WriteStartObject();
        w.WriteString("id", data.Focus.Id);
        w.WriteString("clk", data.Focus.Clk);
        w.WriteEndObject();

        w.WriteStartArray("tasks");
        foreach (var t in data.Tasks)
        {
            w.WriteStartObject();
            w.WriteString("id", t.Id);
            w.WriteString("text", t.Deleted ? "" : t.Text);
            w.WriteString("created", t.Created);
            w.WriteString("completed", t.Completed);
            w.WriteString("order", t.Order);
            w.WriteBoolean("deleted", t.Deleted);
            if (t.Clk.Space != Stamp.Zero) w.WriteString("space", t.Space);
            w.WritePropertyName("clk");
            w.WriteStartObject();
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
        foreach (var s in data.Sheets)
        {
            w.WriteStartObject();
            w.WriteString("id", s.Id);
            w.WriteString("text", s.Deleted ? "" : s.Text);
            w.WriteString("updated", s.Updated);
            w.WriteString("order", s.Order);
            w.WriteBoolean("deleted", s.Deleted);
            if (s.Clk.Space != Stamp.Zero) w.WriteString("space", s.Space);
            w.WritePropertyName("clk");
            w.WriteStartObject();
            w.WriteString("text", s.Clk.Text);
            w.WriteString("order", s.Clk.Order);
            w.WriteString("deleted", s.Clk.Deleted);
            if (s.Clk.Space != Stamp.Zero) w.WriteString("space", s.Clk.Space);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        if (data.Spaces.Count > 0)
        {
            w.WriteStartArray("spaces");
            foreach (var s in data.Spaces)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("name", s.Deleted ? "" : s.Name);
                w.WriteString("order", s.Order);
                w.WriteBoolean("deleted", s.Deleted);
                w.WritePropertyName("clk");
                w.WriteStartObject();
                w.WriteString("name", s.Deleted ? Stamp.Zero : s.Clk.Name);
                w.WriteString("order", s.Clk.Order);
                w.WriteString("deleted", s.Clk.Deleted);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    // ------------------------------------------------------------------ files without clocks

    /// <summary>
    /// A tack.json without clocks (Tack for Windows before 1.2, or the top level of any file).
    /// Desktop records get clocks from their own dates, so re-importing the same file changes
    /// nothing and anything edited here since stays as it is. Port of readDesktop in file.ts.
    /// </summary>
    private static WorkspaceData ReadDesktop(JsonElement doc, IImportTarget ws, long? lastModified)
    {
        var rawTasks = Objects(doc, "tasks");
        var rawSheets = Objects(doc, "sheets");

        // New open tasks go above the current list, in the file's order.
        var openNew = rawTasks.Where(r => !Truthy(r.Value, "completed") && ws.FindTask(JsString(r.Value, "id")) == null).ToList();
        string? key = ws.FirstOpenOrder;
        var keys = new Dictionary<int, string>();
        for (int i = openNew.Count - 1; i >= 0; i--)
        {
            key = key != null ? OrderKey.Before(key) : OrderKey.Mid;
            keys[openNew[i].Index] = key;
        }

        // When the file was saved: as given, or at least as late as the latest date inside it.
        long latest = 0;
        foreach (var (_, r) in rawTasks.Concat(rawSheets))
        {
            foreach (string name in new[] { "created", "completed", "updated" })
                latest = Math.Max(latest, IsoTime.ParseMs(Str(r, name) ?? "") ?? 0);
        }
        long savedAt = lastModified ?? latest;

        // The clock for a field of something this device already has. Unchanged: the same clock (a
        // no-op). Changed, and never edited here (its clock still comes from an earlier import): the
        // desktop's change is the only news, so it wins. Changed in both places: the later one wins,
        // using the file's save time for the desktop's side.
        string ChangedClock(bool same, string localClock) =>
            same ? localClock : Stamp.Node(localClock) == "" ? ws.Clock.Now() : Stamp.FromTime(savedAt);

        string? focusId = null;
        var tasks = new List<TaskData>();
        var usedIds = new HashSet<string>();
        foreach (var (index, r) in rawTasks)
        {
            string text = JsText.CleanTaskText(JsString(r, "text"));
            // Text longer than any device reads is skipped, as in device files.
            if (text.Length == 0 || text.Length > DeviceFile.MaxTaskText) continue;
            string id = Ids.ForImport(Str(r, "id"), usedIds, text);
            long created = IsoTime.ParseMs(JsString(r, "created")) ?? 0;
            long? completed = Truthy(r, "completed") ? IsoTime.ParseMs(JsString(r, "completed")) : null;
            string @base = Stamp.FromTime(created);
            var existing = ws.FindTask(id);
            bool sameDone = existing != null && (existing.Completed != null && existing.Completed.Length > 0) == (completed != null);
            string? rawCreated = Str(r, "created"), rawCompleted = Str(r, "completed");
            tasks.Add(new TaskData(
                id,
                text,
                // Dates as written only if every device reads them so (IsFileDate): any other a device
                // reads as missing, which would reopen a finished task everywhere. Else the date they mean.
                IsFileDate(rawCreated) ? rawCreated! : LocalText(created),
                // Both finished: keep this device's completion time rather than flipping between them.
                completed is long done
                    ? (sameDone && !string.IsNullOrEmpty(existing!.Completed) ? existing.Completed : IsFileDate(rawCompleted) ? rawCompleted! : LocalText(done))
                    : null,
                keys.TryGetValue(index, out var k) ? k : existing?.Order ?? OrderKey.Mid,
                existing?.Deleted ?? false,
                // Files without clocks know nothing of spaces: the task stays where it is here.
                existing?.Space ?? "",
                existing != null
                    ? new TaskClocks(
                        ChangedClock(existing.Text == text, existing.Clk.Text),
                        ChangedClock(sameDone, existing.Clk.Completed),
                        existing.Clk.Order,
                        existing.Clk.Deleted,
                        existing.Clk.Space)
                    : new TaskClocks(@base, completed is long c ? Stamp.FromTime(c) : @base, @base, @base, Stamp.Zero)));
            if (IsTrue(r, "now") && completed == null && focusId == null) focusId = id;
        }

        string? sheetKey = ws.LastSheetOrder;
        string NextKey() => sheetKey = sheetKey != null ? OrderKey.After(sheetKey) : OrderKey.Mid;
        var sheets = new List<SheetData>();
        foreach (var (_, r) in rawSheets)
        {
            string text = JsText.NoteText(JsString(r, "text"));
            if (text.Length > DeviceFile.MaxNoteText) continue;
            string id = Ids.ForImport(Str(r, "id"), usedIds, text);
            long updatedAt = IsoTime.ParseMs(JsString(r, "updated")) ?? 0;
            string? rawUpdated = Str(r, "updated");
            string updated = IsFileDate(rawUpdated) ? rawUpdated! : LocalText(updatedAt);
            var existing = ws.FindSheet(id);
            if (existing != null && existing.Text != text && Stamp.Node(existing.Clk.Text) != "")
            {
                // Written here and on the desktop: keep both, the desktop's as a copy beside this one.
                // The copy's id comes from its content, so importing the same file again adds nothing.
                id = Ids.Derived($"{id}:{text}");
                if (ws.FindSheet(id) != null) continue;
                string c = Stamp.FromTime(updatedAt);
                sheets.Add(new SheetData(id, text, updated, NextKey(), false, "", SheetClocks.All(c)));
                continue;
            }
            if (existing != null)
            {
                // Unchanged (a no-op), or only ever changed on the desktop (its version wins).
                string clk = existing.Text == text ? existing.Clk.Text : ws.Clock.Now();
                sheets.Add(existing with
                {
                    Text = text,
                    Updated = existing.Text == text ? existing.Updated : updated,
                    Clk = existing.Clk with { Text = clk },
                });
                continue;
            }
            string stamp = Stamp.FromTime(updatedAt);
            sheets.Add(new SheetData(id, text, updated, NextKey(), false, "", SheetClocks.All(stamp)));
        }

        // Importing is an explicit "bring this in": the file's focus task becomes the focus here too
        // (unless it already is, so importing the same file twice still changes nothing).
        var focus = focusId != null && ws.FocusId != focusId ? new FocusData(focusId, ws.Clock.Now()) : FocusData.None;
        return new WorkspaceData(tasks, sheets, focus, []);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>isoLocal(new Date(ms)): a date in this computer's time zone.</summary>
    public static string LocalText(long ms) => IsoTime.Format(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), IsoTime.Zone));

    private static string EpochText() => LocalText(0);

    /// <summary>
    /// Dates in records: ISO 8601 as both apps write them, and short (isFileDate in file.ts, same
    /// pattern). Anything else counts as missing: an odd "created" would otherwise win every merge
    /// for good, since the earliest creation date wins.
    /// </summary>
    public static bool IsFileDate(string? value) => value is { Length: <= 40 } && FileDatePattern().IsMatch(value);

    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt][0-9]{2}:[0-9]{2}(?::[0-9]{2})?(?:[.,][0-9]+)?(?:[Zz]|[+-][0-9]{2}:?[0-9]{2})?\z")]
    private static partial System.Text.RegularExpressions.Regex FileDatePattern();

    private static bool IsDeviceId(string id) => id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' or '-');

    private static bool IsArray(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array;

    /// <summary>The length of an array property (0 if it's missing or not an array).</summary>
    private static int Length(JsonElement obj, string name) => IsArray(obj, name) ? obj.GetProperty(name).GetArrayLength() : 0;

    /// <summary>The objects of an array property, with their positions.</summary>
    private static List<(int Index, JsonElement Value)> Objects(JsonElement obj, string name)
    {
        var list = new List<(int, JsonElement)>();
        if (!IsArray(obj, name)) return list;
        int i = 0;
        foreach (var v in obj.GetProperty(name).EnumerateArray())
        {
            if (v.ValueKind == JsonValueKind.Object) list.Add((i, v));
            i++;
        }
        return list;
    }

    /// <summary>A property that is a string, else null.</summary>
    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsTrue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>String(r[name] ?? '') for the values a tack.json can hold.</summary>
    private static string JsString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()!,
            JsonValueKind.Number => v.TryGetDouble(out double d) ? JsNumber(d) : v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Object => "[object Object]",
            _ => "",
        };
    }

    private static string JsNumber(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 1e21 ? d.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>JavaScript truthiness of r[name].</summary>
    private static bool Truthy(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()!.Length > 0,
            JsonValueKind.Number => v.TryGetDouble(out double d) && d != 0 && !double.IsNaN(d),
            JsonValueKind.True or JsonValueKind.Object or JsonValueKind.Array => true,
            _ => false,
        };
    }
}
