using System.Text.Json;
using Tack.Model;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>Reads records and views out of the vectors, as written by Tack Focus (no checks: these are known good).</summary>
internal static class Json
{
    public static string? Str(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long Long(this JsonElement e, string name) => (long)e.GetProperty(name).GetDouble();

    public static bool Has(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    public static string FromUnits(JsonElement units) => new(units.EnumerateArray().Select(u => (char)u.GetInt32()).ToArray());

    public static TaskData? Task(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : new TaskData(
        e.Str("id")!,
        e.Str("text")!,
        e.Str("created")!,
        e.Str("completed"),
        e.Str("order")!,
        e.GetProperty("deleted").GetBoolean(),
        new TaskClocks(
            e.GetProperty("clk").Str("text")!,
            e.GetProperty("clk").Str("completed")!,
            e.GetProperty("clk").Str("order")!,
            e.GetProperty("clk").Str("deleted")!));

    public static SheetData? Sheet(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : new SheetData(
        e.Str("id")!,
        e.Str("text")!,
        e.Str("updated")!,
        e.Str("order")!,
        e.GetProperty("deleted").GetBoolean(),
        new SheetClocks(
            e.GetProperty("clk").Str("text")!,
            e.GetProperty("clk").Str("order")!,
            e.GetProperty("clk").Str("deleted")!));

    public static FocusData Focus(JsonElement e) => new(e.Str("id"), e.Str("clk")!);

    public static WorkspaceData Data(JsonElement e) => new(
        e.GetProperty("tasks").EnumerateArray().Select(t => Task(t)!).ToList(),
        e.GetProperty("sheets").EnumerateArray().Select(s => Sheet(s)!).ToList(),
        Focus(e.GetProperty("focus")));

    /// <summary>The lists as both apps show them: focus task, open, done, sheets (ids).</summary>
    public static string Views(JsonElement e)
    {
        static string Ids(JsonElement a) => string.Join(",", a.EnumerateArray().Select(x => x.GetString()));
        return $"now={e.Str("now") ?? "-"} open=[{Ids(e.GetProperty("open"))}] done=[{Ids(e.GetProperty("done"))}] sheets=[{Ids(e.GetProperty("sheets"))}]";
    }

    public static string Views(Workspace ws) =>
        $"now={ws.Now?.Id ?? "-"} open=[{string.Join(",", ws.Open.Select(t => t.Id))}] done=[{string.Join(",", ws.Done.Select(t => t.Id))}] sheets=[{string.Join(",", ws.Sheets.Select(s => s.Id))}]";
}
