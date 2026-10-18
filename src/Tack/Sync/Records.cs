namespace Tack.Sync;

/// <summary>
/// The records both apps exchange (web/src/model/types.ts). The first fields mirror tack.json's top
/// level; the rest make it safe to merge edits from several devices: a sort key instead of a list
/// position, a tombstone instead of removal, and a clock per field so independent edits never
/// overwrite each other. Dates are kept as the ISO text they arrived with.
/// </summary>
internal sealed record TaskData(
    string Id,
    string Text,
    string Created,
    string? Completed,
    string Order,
    bool Deleted,
    TaskClocks Clk);

internal sealed record TaskClocks(string Text, string Completed, string Order, string Deleted)
{
    public static TaskClocks All(string stamp) => new(stamp, stamp, stamp, stamp);

    public string Max() => Stamp.Max(Stamp.Max(Stamp.Max(Stamp.Max(Stamp.Zero, Text), Completed), Order), Deleted);
}

/// <summary>A sheet of scratch paper; its title is its first line.</summary>
internal sealed record SheetData(
    string Id,
    string Text,
    string Updated,
    string Order,
    bool Deleted,
    SheetClocks Clk);

internal sealed record SheetClocks(string Text, string Order, string Deleted)
{
    public static SheetClocks All(string stamp) => new(stamp, stamp, stamp);

    public string Max() => Stamp.Max(Stamp.Max(Stamp.Max(Stamp.Zero, Text), Order), Deleted);
}

/// <summary>The single task in focus ("Now"), as one register so devices can't disagree.</summary>
internal sealed record FocusData(string? Id, string Clk)
{
    public static readonly FocusData None = new(null, Stamp.Zero);
}

internal sealed record WorkspaceData(IReadOnlyList<TaskData> Tasks, IReadOnlyList<SheetData> Sheets, FocusData Focus)
{
    public static readonly WorkspaceData Empty = new([], [], FocusData.None);
}
