using System.IO;
using System.Text.Json;
using Tack.Model;
using Tack.Services;
using Tack.Sync;

namespace Tack.Tests;

/// <summary>
/// Tack for Windows' sync, driven from Tack Focus's browser tests (web/tests/e2e/interop.spec.ts):
/// a PC whose records live in pc.json beside the folder syncs through the folder, adds a task,
/// syncs again, and prints what it shows. Same engine, folder provider and device files as the app.
/// </summary>
internal static class Interop
{
    public static int Run(string folder, string task)
    {
        string state = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(folder))!, "pc.json");
        var data = File.Exists(state) ? Json.Data(JsonDocument.Parse(File.ReadAllBytes(state)).RootElement.GetProperty("data")) : null;
        var meta = File.Exists(state)
            ? JsonSerializer.Deserialize(JsonDocument.Parse(File.ReadAllBytes(state)).RootElement.GetProperty("meta").GetRawText(), SyncJsonContext.Default.SyncMeta)!
            : SyncMeta.Empty(folder);
        var ws = new Workspace(new Settings { SyncFolder = folder }, new Hlc("pcINTEROP"), data);
        var local = new LocalSyncFolder(folder);
        local.Prepare();
        var engine = new SyncEngine(ws, local, new SyncHooks
        {
            Label = () => ("Tack for Windows on INTEROP-PC", "Tack for Windows test"),
            FlushAsync = () => Task.CompletedTask,
            SaveMetaAsync = _ => Task.CompletedTask,
        });
        engine.CycleAsync(meta, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).GetAwaiter().GetResult();
        ws.AddTask(task);
        engine.CycleAsync(meta, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).GetAwaiter().GetResult();

        using (var file = File.Create(state))
        using (var w = new Utf8JsonWriter(file))
        {
            w.WriteStartObject();
            w.WritePropertyName("data");
            JsonSerializer.Serialize(w, ToJson(ws.ToData()));
            w.WritePropertyName("meta");
            JsonSerializer.Serialize(w, meta, SyncJsonContext.Default.SyncMeta);
            w.WriteEndObject();
        }
        foreach (var t in ws.Open) Console.WriteLine($"open: {t.Text}");
        if (ws.Now is { } now) Console.WriteLine($"now: {now.Text}");
        return 0;
    }

    private static object ToJson(WorkspaceData d) => new Dictionary<string, object?>
    {
        ["focus"] = new Dictionary<string, object?> { ["id"] = d.Focus.Id, ["clk"] = d.Focus.Clk },
        ["tasks"] = d.Tasks.Select(t => new Dictionary<string, object?>
        {
            ["id"] = t.Id, ["text"] = t.Text, ["created"] = t.Created, ["completed"] = t.Completed, ["order"] = t.Order, ["deleted"] = t.Deleted, ["space"] = t.Space,
            ["clk"] = new Dictionary<string, string> { ["text"] = t.Clk.Text, ["completed"] = t.Clk.Completed, ["order"] = t.Clk.Order, ["deleted"] = t.Clk.Deleted, ["space"] = t.Clk.Space },
        }).ToList(),
        ["sheets"] = d.Sheets.Select(s => new Dictionary<string, object?>
        {
            ["id"] = s.Id, ["text"] = s.Text, ["updated"] = s.Updated, ["order"] = s.Order, ["deleted"] = s.Deleted, ["space"] = s.Space,
            ["clk"] = new Dictionary<string, string> { ["text"] = s.Clk.Text, ["order"] = s.Clk.Order, ["deleted"] = s.Clk.Deleted, ["space"] = s.Clk.Space },
        }).ToList(),
        ["spaces"] = d.Spaces.Select(s => new Dictionary<string, object?>
        {
            ["id"] = s.Id, ["name"] = s.Name, ["order"] = s.Order, ["deleted"] = s.Deleted,
            ["clk"] = new Dictionary<string, string> { ["name"] = s.Clk.Name, ["order"] = s.Clk.Order, ["deleted"] = s.Clk.Deleted },
        }).ToList(),
    };
}
