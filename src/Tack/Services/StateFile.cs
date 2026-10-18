using System.Text.Json;
using System.Text.Json.Serialization;
using Tack.Model;
using Tack.Sync;

namespace Tack.Services;

/// <summary>
/// tack.json's "tackFocus" section: the records with their clocks, sort keys and tombstones, and
/// the focus register, exactly as Tack Focus exports them. Older versions of Tack ignore it.
/// When read it is kept as raw JSON (its records are checked one by one, so a damaged record never
/// stops the file from loading); when written it comes from the workspace.
/// </summary>
[JsonConverter(typeof(TackFocusSectionConverter))]
internal sealed class TackFocusSection
{
    public JsonElement Raw { get; init; }

    public WorkspaceData? Data { get; init; }

    public string Exported { get; init; } = "";

    public string? Device { get; init; }
}

internal sealed class TackFocusSectionConverter : JsonConverter<TackFocusSection>
{
    public override TackFocusSection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new() { Raw = JsonElement.ParseValue(ref reader) };

    public override void Write(Utf8JsonWriter writer, TackFocusSection value, JsonSerializerOptions options)
    {
        if (value.Data is { } data) TackFile.WriteSection(writer, data, value.Exported, value.Device);
        else if (value.Raw.ValueKind != JsonValueKind.Undefined) value.Raw.WriteTo(writer);
        else writer.WriteNullValue();
    }
}

/// <summary>
/// Turns Tack's own tack.json into records and back. The top level stays exactly what every Tack
/// version reads and writes, and it is what you see: the section adds the clocks. If the two
/// disagree (the file was edited by hand), the top level wins, as a fresh edit.
/// </summary>
internal static class StateFile
{
    public static StateDocument ToDocument(Workspace ws)
    {
        var doc = new StateDocument { Settings = ws.Settings };
        if (ws.Now is { } now) doc.Tasks.Add(ToRecord(now, isNow: true));
        foreach (var t in ws.Open) doc.Tasks.Add(ToRecord(t, false));
        foreach (var t in ws.Done) doc.Tasks.Add(ToRecord(t, false));
        foreach (var s in ws.Sheets) doc.Sheets.Add(new SheetRecord { Id = s.Id, Text = s.Text, Updated = s.UpdatedAt });

        var data = ws.ToData();
        doc.TackFocus = new TackFocusSection { Data = data, Exported = Exported(data), Device = ws.Clock.Node };
        return doc;
    }

    /// <summary>The device id saved with the file, if any.</summary>
    public static string? Device(StateDocument doc) => doc.TackFocus is { } section ? TackFile.ReadDevice(section.Raw) : null;

    /// <summary>
    /// The records a document holds. A file without the section (Tack 1.1 and earlier, or saved by
    /// one since) gets clocks from its own dates and keys in its own order, the same ones Tack Focus
    /// gives it when it imports that file, so the two agree without any change on either side.
    /// </summary>
    /// <param name="fileMs">When the file was saved: the time the focus task (which has no date of its own) was set.</param>
    public static WorkspaceData ToData(StateDocument doc, Hlc clock, long fileMs, long nowMs)
    {
        var section = doc.TackFocus is { } raw ? TackFile.ReadSection(raw.Raw, nowMs, clean: false) : null;
        if (section != null)
        {
            foreach (var t in section.Tasks) clock.Observe(t.Clk.Max());
            foreach (var s in section.Sheets) clock.Observe(s.Clk.Max());
            clock.Observe(section.Focus.Clk);
        }

        // ---- Tasks: the section's records, in their order, then any the top level adds ----
        var tasks = new List<TaskData>();
        var taskAt = new Dictionary<string, int>();
        foreach (var t in section?.Tasks ?? [])
        {
            if (taskAt.TryGetValue(t.Id, out int i)) tasks[i] = Merge.Task(tasks[i], t).Value;
            else
            {
                taskAt[t.Id] = tasks.Count;
                tasks.Add(t);
            }
        }
        int known = tasks.Count;

        var seen = new HashSet<string>();
        var chain = new List<int>(); // open tasks, in the top level's order, whose keys must follow it
        string? nowId = null;
        foreach (var r in doc.Tasks)
        {
            if (string.IsNullOrWhiteSpace(r.Text)) continue;
            string id = Ids.IsValid(r.Id) && !seen.Contains(r.Id) ? r.Id : Ids.NewId();
            seen.Add(id);
            bool done = r.Completed != null;
            if (taskAt.TryGetValue(id, out int at) && at < known)
            {
                var rec = tasks[at];
                if (rec.Text != r.Text) rec = rec with { Text = r.Text, Clk = rec.Clk with { Text = clock.Now() } };
                if ((rec.Completed != null) != done)
                    rec = rec with { Completed = done ? IsoTime.FormatTrimmed(r.Completed!.Value) : null, Clk = rec.Clk with { Completed = clock.Now() } };
                if (rec.Deleted) rec = rec with { Deleted = false, Clk = rec.Clk with { Deleted = clock.Now() } };
                tasks[at] = rec;
            }
            else
            {
                string @base = Stamp.FromTime(r.Created.ToUnixTimeMilliseconds());
                at = tasks.Count;
                taskAt[id] = at;
                tasks.Add(new TaskData(
                    id,
                    r.Text,
                    IsoTime.FormatTrimmed(r.Created),
                    done ? IsoTime.FormatTrimmed(r.Completed!.Value) : null,
                    OrderKey.Mid,
                    false,
                    new TaskClocks(@base, done ? Stamp.FromTime(r.Completed!.Value.ToUnixTimeMilliseconds()) : @base, @base, @base)));
            }
            if (done) continue;
            if (r.Now && nowId == null)
            {
                nowId = id;
                if (at < known) continue; // in focus, its key doesn't place it anywhere
            }
            chain.Add(at);
        }

        // Records the top level no longer has were deleted there.
        for (int i = 0; i < known; i++)
        {
            if (!seen.Contains(tasks[i].Id) && !tasks[i].Deleted)
                tasks[i] = tasks[i] with { Deleted = true, Clk = tasks[i].Clk with { Deleted = clock.Now() } };
        }

        var keys = OrderKey.Fit(chain.Select(i => (i < known ? tasks[i].Order : null, tasks[i].Id)).ToList(), anchorTop: false);
        for (int k = 0; k < chain.Count; k++)
        {
            if (keys[k] is not { } key) continue;
            int i = chain[k];
            tasks[i] = i < known
                ? tasks[i] with { Order = key, Clk = tasks[i].Clk with { Order = clock.Now() } }
                : tasks[i] with { Order = key };
        }

        FocusData focus;
        if (section != null)
        {
            var f = section.Focus;
            string? effective = f.Id != null && taskAt.TryGetValue(f.Id, out int fi) && !tasks[fi].Deleted && tasks[fi].Completed == null ? f.Id : null;
            focus = effective == nowId ? f : new FocusData(nowId, clock.Now());
        }
        else
        {
            focus = nowId != null ? new FocusData(nowId, Stamp.FromTime(fileMs)) : FocusData.None;
        }

        // ---- Sheets, the same way ----
        var sheets = new List<SheetData>();
        var sheetAt = new Dictionary<string, int>();
        foreach (var s in section?.Sheets ?? [])
        {
            if (sheetAt.TryGetValue(s.Id, out int i)) sheets[i] = Merge.Sheet(sheets[i], s).Value;
            else
            {
                sheetAt[s.Id] = sheets.Count;
                sheets.Add(s);
            }
        }
        int knownSheets = sheets.Count;

        var seenSheets = new HashSet<string>();
        var sheetChain = new List<int>();
        foreach (var r in doc.Sheets)
        {
            // The top level has the TextBox's "\r\n"; a note that differs only in line breaks isn't a hand edit.
            string text = JsText.NoteText(r.Text ?? "");
            string id = Ids.IsValid(r.Id) && !seenSheets.Contains(r.Id) ? r.Id : Ids.NewId();
            seenSheets.Add(id);
            if (sheetAt.TryGetValue(id, out int at) && at < knownSheets)
            {
                var rec = sheets[at];
                if (rec.Text != text)
                    rec = rec with { Text = text, Updated = IsoTime.FormatTrimmed(r.Updated), Clk = rec.Clk with { Text = clock.Now() } };
                if (rec.Deleted) rec = rec with { Deleted = false, Clk = rec.Clk with { Deleted = clock.Now() } };
                sheets[at] = rec;
            }
            else
            {
                at = sheets.Count;
                sheetAt[id] = at;
                sheets.Add(new SheetData(id, text, IsoTime.FormatTrimmed(r.Updated), OrderKey.Mid, false,
                    SheetClocks.All(Stamp.FromTime(r.Updated.ToUnixTimeMilliseconds()))));
            }
            sheetChain.Add(at);
        }
        for (int i = 0; i < knownSheets; i++)
        {
            if (!seenSheets.Contains(sheets[i].Id) && !sheets[i].Deleted)
                sheets[i] = sheets[i] with { Deleted = true, Clk = sheets[i].Clk with { Deleted = clock.Now() } };
        }
        var sheetKeys = OrderKey.Fit(sheetChain.Select(i => (i < knownSheets ? sheets[i].Order : null, sheets[i].Id)).ToList(), anchorTop: true);
        for (int k = 0; k < sheetChain.Count; k++)
        {
            if (sheetKeys[k] is not { } key) continue;
            int i = sheetChain[k];
            sheets[i] = i < knownSheets
                ? sheets[i] with { Order = key, Clk = sheets[i].Clk with { Order = clock.Now() } }
                : sheets[i] with { Order = key };
        }

        return new WorkspaceData(tasks, sheets, focus);
    }

    private static TaskRecord ToRecord(TaskItem t, bool isNow) => new()
    {
        Id = t.Id, Text = t.Text, Created = t.CreatedAt, Completed = t.CompletedAt, Now = isNow,
    };

    /// <summary>When the data was last changed (its newest clock), in local time.</summary>
    private static string Exported(WorkspaceData data)
    {
        string newest = data.Focus.Clk;
        foreach (var t in data.Tasks) newest = Stamp.Max(newest, t.Clk.Max());
        foreach (var s in data.Sheets) newest = Stamp.Max(newest, s.Clk.Max());
        double ms = Math.Clamp(Stamp.Time(newest), 0, 253_402_300_799_000);
        return TackFile.LocalText((long)ms);
    }
}
