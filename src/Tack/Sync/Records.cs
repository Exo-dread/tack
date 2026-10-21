namespace Tack.Sync;

/// <summary>
/// The records both apps exchange (web/src/model/types.ts). The first fields mirror tack.json's top
/// level; the rest make it safe to merge edits from several devices: a sort key instead of a list
/// position, a tombstone instead of removal, and a clock per field so independent edits never
/// overwrite each other. Dates are kept as the ISO text they arrived with.
/// </summary>
/// <param name="Space">
/// The space it belongs to: "" if never assigned (Main), else a space id ("main" for an explicit
/// move back to Main). "" always goes with a Zero clock, and a Zero clock with "" (see <see cref="SpaceField"/>).
/// </param>
internal sealed record TaskData(
    string Id,
    string Text,
    string Created,
    string? Completed,
    string Order,
    bool Deleted,
    string Space,
    TaskClocks Clk);

/// <summary>Clocks per field. <see cref="All"/> leaves Space at Zero: a new record is in no space yet.</summary>
internal sealed record TaskClocks(string Text, string Completed, string Order, string Deleted, string Space)
{
    public static TaskClocks All(string stamp) => new(stamp, stamp, stamp, stamp, Stamp.Zero);

    public string Max() => Stamp.Max(Stamp.Max(Stamp.Max(Stamp.Max(Stamp.Max(Stamp.Zero, Text), Completed), Order), Deleted), Space);
}

/// <summary>A sheet of scratch paper; its title is its first line.</summary>
internal sealed record SheetData(
    string Id,
    string Text,
    string Updated,
    string Order,
    bool Deleted,
    string Space,
    SheetClocks Clk);

internal sealed record SheetClocks(string Text, string Order, string Deleted, string Space)
{
    public static SheetClocks All(string stamp) => new(stamp, stamp, stamp, Stamp.Zero);

    public string Max() => Stamp.Max(Stamp.Max(Stamp.Max(Stamp.Max(Stamp.Zero, Text), Order), Deleted), Space);
}

/// <summary>
/// A space (Work, Dev, Personal...) that tasks and sheets can be put in. Main, where everything
/// starts, needs no record; it gets one (id "main") only once it's renamed.
/// </summary>
internal sealed record SpaceData(string Id, string Name, string Order, bool Deleted, SpaceClocks Clk);

internal sealed record SpaceClocks(string Name, string Order, string Deleted)
{
    public static SpaceClocks All(string stamp) => new(stamp, stamp, stamp);

    public string Max() => Stamp.Max(Stamp.Max(Stamp.Max(Stamp.Zero, Name), Order), Deleted);
}

/// <summary>The single task in focus ("Now"), as one register so devices can't disagree.</summary>
internal sealed record FocusData(string? Id, string Clk)
{
    public static readonly FocusData None = new(null, Stamp.Zero);
}

internal sealed record WorkspaceData(IReadOnlyList<TaskData> Tasks, IReadOnlyList<SheetData> Sheets, FocusData Focus, IReadOnlyList<SpaceData> Spaces)
{
    public static readonly WorkspaceData Empty = new([], [], FocusData.None, []);
}

/// <summary>
/// The <c>space</c> field of tasks and sheets, and what it resolves to (effectiveSpace in Tack Focus).
/// </summary>
internal static class SpaceField
{
    /// <summary>Main's id, reserved: an explicit move back to Main, and Main's own record once it's renamed.</summary>
    public const string Main = "main";

    /// <summary>The longest space name, in UTF-16 units (MAX_SPACE_NAME in Tack Focus, which counts the same).</summary>
    public const int MaxName = 100;

    /// <summary>
    /// A space name as every device keeps it, wherever it comes from (typed, a file, a snapshot):
    /// one line, and no longer than <see cref="MaxName"/>, cut where a character ends (an emoji is
    /// never halved) and tidied again (cleanSpaceName in Tack Focus). Cleaning a clean name changes
    /// nothing, so devices that read the same name hold the same name.
    /// </summary>
    public static string CleanName(string raw)
    {
        string name = JsText.CleanTaskText(raw);
        if (name.Length <= MaxName) return name;
        return JsText.CleanTaskText(name[..(char.IsHighSurrogate(name[MaxName - 1]) ? MaxName - 1 : MaxName)]);
    }

    /// <summary>
    /// A space and its clock as a file gives them: a valid id with a plausible, non-Zero clock, or
    /// else ("", Zero). A record is never refused over these two fields. An id that names no known
    /// space is kept (its space record may not have arrived yet).
    /// </summary>
    public static (string Space, string Clk) Read(string? space, string? clk, long nowMs) =>
        Ids.IsValid(space) && clk != Stamp.Zero && Stamp.IsPlausible(clk, nowMs) ? (space!, clk!) : ("", Stamp.Zero);

    /// <summary>The same rule for records already in memory: "" goes with Zero, and Zero with "".</summary>
    public static (string Space, string Clk) Normal(string? space, string? clk) =>
        Ids.IsValid(space) && clk != Stamp.Zero && Stamp.IsStamp(clk) ? (space!, clk!) : ("", Stamp.Zero);

    public static TaskData Normal(TaskData t)
    {
        var (space, clk) = Normal(t.Space, t.Clk.Space);
        return space == t.Space && clk == t.Clk.Space ? t : t with { Space = space, Clk = t.Clk with { Space = clk } };
    }

    public static SheetData Normal(SheetData s)
    {
        var (space, clk) = Normal(s.Space, s.Clk.Space);
        return space == s.Space && clk == s.Clk.Space ? s : s with { Space = space, Clk = s.Clk with { Space = clk } };
    }
}

/// <summary>Spaces as the lists will show them (no UI uses them yet).</summary>
internal static class Spaces
{
    /// <summary>
    /// The space a task or sheet shows in: its own, if that's a live space; else Main (never
    /// assigned, moved back to Main, its space deleted, or a space not known here yet).
    /// </summary>
    public static string Effective(string space, IEnumerable<SpaceData> spaces)
    {
        if (string.IsNullOrEmpty(space) || space == SpaceField.Main) return SpaceField.Main;
        foreach (var s in spaces)
        {
            if (s.Id == space && !s.Deleted) return space;
        }
        return SpaceField.Main;
    }
}
